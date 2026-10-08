using System.Globalization;
using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;

namespace LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;

public sealed class ProcesarLoteSiniestroHandler(
    ISiniestroSICASClient sicasClient,
    ISICASRestClient sicasRestClient,
    ISiniestroRepository siniestroRepo,
    IDocumentService documentService,
    GuardarSiniestroHandler guardarHandler,
    ProcesarLoteSeguroHandler segurosHandler,
    IBitacoraRepository bitacora,
    IMonitoreoErrores monitoreo,
    IOptions<AplicacionOptions> opciones,
    ILogger<ProcesarLoteSiniestroHandler> log)
{
    private readonly int _idAplicacion = opciones.Value.Siniestros;

    // Contadores de la corrida. Sirven para reportar los fallos de los servicios externos UNA vez,
    // con su conteo, en lugar de una vez por documento: con el FTP caído, el reporte individual
    // generaba cientos de eventos idénticos por noche sobre un problema ya conocido, y esa
    // avalancha es justo lo que acaba haciendo que nadie mire las alertas.
    // Son campos de instancia y no estáticos porque el handler se resuelve una vez por corrida.
    private int _documentosIntentados;
    private int _documentosFallidos;

    public async Task Handle(ProcesarLoteSiniestroCommand cmd, CancellationToken ct = default)
    {
        // Ver comentario equivalente en ProcesarLoteSeguroHandler: distinguir barrido automático
        // de reprocesamiento manual es lo primero que se necesita saber ante una alerta.
        monitoreo.Etiquetar("modulo", "Siniestros");

        if (!string.IsNullOrWhiteSpace(cmd.FolioSiniestro))
        {
            monitoreo.Etiquetar("disparador", "manual-folio");
            await ProcesarPorReporte(cmd.FolioSiniestro, ct);
            return;
        }

        // La reconciliación (Fase 3) revisa en SICAS TODOS los siniestros abiertos —más de mil—,
        // así que solo tiene sentido en el barrido amplio, el que corre sin rango explícito una vez
        // al día. El servicio de intervalo llama a este mismo Handle con una ventana de minutos y
        // puede ejecutarse cada 20: dejarle la Fase 3 significaría repetir esas mil consultas a
        // SICAS cada vez, con el throttling asegurado.
        bool esBarridoCompleto = cmd.Desde is null && cmd.Hasta is null;

        await ProcesarLote(cmd.Desde ?? DateTime.Now.AddDays(-1), cmd.Hasta ?? DateTime.Now,
            esBarridoCompleto, ct);
    }

    private async Task ProcesarLote(DateTime desde, DateTime hasta, bool reconciliar, CancellationToken ct)
    {
        log.LogInformation("Iniciando lote Siniestros {Desde:dd/MM/yyyy} → {Hasta:dd/MM/yyyy}", desde, hasta);

        monitoreo.Etiquetar("rango_desde", desde.ToString("dd/MM/yyyy HH:mm"));
        monitoreo.Etiquetar("rango_hasta", hasta.ToString("dd/MM/yyyy HH:mm"));

        int ultimaPagina = 0;
        int totalRegistros = 0;
        for (int pagina = 1; pagina <= 30; pagina++)
        {
            List<SiniestroResumenSICAS> siniestros;
            try
            {
                siniestros = await sicasClient.BuscarSiniestrosVigentes(desde, hasta, pagina, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Una página que falle no puede llevarse por delante las que vienen detrás: se
                // anota y se sigue. Con `break` se perdería todo el resto del rango.
                monitoreo.Capturar(ex, "siniestros.barrido",
                    ("pagina", pagina.ToString()), ("consecuencia", "pagina-omitida-el-lote-continua"));
                log.LogError(ex, "Error leyendo la página {Pagina} del barrido de siniestros; se continúa.", pagina);
                continue;
            }

            if (siniestros.Count == 0) break;

            ultimaPagina = pagina;
            totalRegistros += siniestros.Count;
            log.LogInformation("Página {Pagina}: {Count} siniestros", pagina, siniestros.Count);
            monitoreo.Rastrear("siniestros.barrido", $"Página {pagina}: {siniestros.Count} siniestros",
                ("pagina", pagina.ToString()), ("registros", siniestros.Count.ToString()));

            foreach (var siniestro in siniestros)
            {
                await ProcesarSiniestroCompleto(siniestro, ct);
                await Task.Delay(300, ct); // evita ráfagas hacia SICAS (throttling observado en Seguros)
            }
        }

        if (ultimaPagina == 30)
        {
            string msg = $"Barrido de siniestros {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                         "(3,000 registros) — es posible que existan más siniestros sin procesar en este rango.";
            log.LogWarning(msg);
            await RegistrarEnBitacora(msg, NivelBitacora.Aviso, ct);
        }

        // Fase 2: comentarios de bitácora del rango
        await ProcesarBitacoraDia(desde, hasta, ct);

        // Fase 3: poner al día lo que el barrido por fechas no puede ver (ver ReconciliarAbiertos).
        if (reconciliar)
            await ReconciliarAbiertos(ct);
        else
            log.LogInformation("Fase 3 omitida: este lote tiene rango explícito, la reconciliación corre en el barrido diario.");

        // Ver comentario equivalente en ProcesarLoteSeguroHandler.
        monitoreo.Etiquetar("registros_encontrados", totalRegistros.ToString());

        ReportarResumenDocumentos();

        log.LogInformation("Lote Siniestros finalizado. {Total} siniestros encontrados en el rango.", totalRegistros);
    }

    private async Task ProcesarPorReporte(string noReporte, CancellationToken ct)
    {
        log.LogInformation("Procesando siniestro por folio: {NoReporte}", noReporte);

        var resultados = await sicasClient.BuscarPorReporte(noReporte, ct);
        if (resultados.Count == 0)
        {
            log.LogWarning("Siniestro {NoReporte} no encontrado en SICAS.", noReporte);
            await RegistrarEnBitacora($"Siniestro {noReporte} no encontrado en SICAS.",
                NivelBitacora.Aviso, ct);
            return;
        }

        // Se procesan TODOS, no solo el primero: un mismo numero de reporte puede corresponder a
        // varios siniestros de SICAS, cada uno en su propia poliza, y quedarse con resultados[0]
        // dejaba a los demas sin sincronizar para siempre.
        if (resultados.Count > 1)
            log.LogInformation("SICAS devuelve {Total} siniestros para el folio {NoReporte}; se procesan todos.",
                resultados.Count, noReporte);

        foreach (var resultado in resultados)
            await ProcesarSiniestroCompleto(resultado, ct);
    }

    private async Task ProcesarSiniestroCompleto(SiniestroResumenSICAS siniestro, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siniestro.NumReporte)) return;

        // Ámbito por siniestro: el folio y los ids de SICAS viajan con cualquier error de las
        // capas de abajo y se descartan al pasar al siguiente (ver ProcesarLoteSeguroHandler).
        using var ambito = monitoreo.IniciarAmbito("siniestros.procesar-siniestro",
            ("folio_siniestro", siniestro.NumReporte),
            ("iddocto", siniestro.IDDocto?.ToString()),
            ("idsiniestro", siniestro.IDSiniestro?.ToString()));

        try
        {
            if (siniestro.IDDocto is null)
            {
                log.LogWarning("Siniestro {NoReporte}: sin IDDocto, no se puede resolver la serie.", siniestro.NumReporte);
                monitoreo.ReportarFalloSilencioso("siniestros.procesar-siniestro",
                    "HDS00009 devolvió el siniestro sin IDDocto: no hay forma de resolver la serie",
                    "siniestro-no-guardado",
                    ("folio_siniestro", siniestro.NumReporte));
                return;
            }

            // HDS00009 no trae la serie del vehículo — sale de una consulta aparte (HWS_DDETAIL),
            // igual que en Seguros (mismo patrón que el ETL legacy: PolizaDetalle es un objeto separado).
            var detalle = await sicasClient.BuscarDetalle(siniestro.IDDocto.Value, ct);
            if (detalle?.Serie is null)
            {
                log.LogWarning("Siniestro {NoReporte}: no se pudo resolver la serie del vehículo (IDDocto={IDDocto}).",
                    siniestro.NumReporte, siniestro.IDDocto);

                // Ojo: esto NO es el caso "no pertenece a la flotilla" (ése se filtra más abajo y
                // es legítimo). Aquí HWS_DDETAIL no devolvió nada, así que ni siquiera se llegó a
                // saber de quién era el vehículo.
                monitoreo.ReportarFalloSilencioso("siniestros.procesar-siniestro",
                    "HWS_DDETAIL no devolvió la serie del vehículo",
                    "siniestro-no-guardado",
                    ("folio_siniestro", siniestro.NumReporte),
                    ("iddocto", siniestro.IDDocto?.ToString()));
                return;
            }

            if (!await siniestroRepo.ExisteVehiculoAsync(detalle.Serie, ct))
            {
                log.LogInformation("Siniestro {NoReporte} (serie {Serie}) no pertenece a la flotilla propia; se omite.",
                    siniestro.NumReporte, detalle.Serie);
                return;
            }

            // No se obtiene bitácora aquí: SICAS solo expone el ClaveBit de un siniestro vía una
            // operación SOAP sin equivalente REST confirmado (WS_Siniestros + parseo de texto).
            // El historial de estatus se acumula de forma incremental vía ProcesarBitacoraDia
            // (Fase 2, H03314011 — sí funciona por REST y no depende de ClaveBit).
            var archivos = siniestro.IDSiniestro.HasValue
                ? await sicasClient.BuscarDigital(siniestro.IDSiniestro.Value, ct)
                : [];

            monitoreo.Etiquetar("serie", detalle.Serie);

            var cmd = ConstruirComando(siniestro, detalle);
            var resultado = await GuardarTrayendoPolizaSiFalta(cmd, siniestro.NumReporte, ct);

            if (!resultado.Exitoso)
            {
                if (resultado.Omitido)
                {
                    log.LogInformation("Siniestro {NoReporte} omitido: {Mensaje}", siniestro.NumReporte, resultado.Mensaje);
                    return;
                }

                log.LogError("Error guardando siniestro {NoReporte}: {Mensaje}",
                    siniestro.NumReporte, resultado.Mensaje);
                await RegistrarEnBitacora(
                    $"Error guardando siniestro {siniestro.NumReporte}: {resultado.Mensaje}",
                    NivelBitacora.Error, ct);
                return;
            }

            // El estatus y el historial, con lo que SICAS reporta AHORA. Sin esto, guardar el
            // siniestro dejaba sus datos al día pero su estatus intacto: reprocesar un folio a mano
            // —lo que pide operaciones cuando ve uno mal— no lo arreglaba, porque la corrección
            // vivía solo en la Fase 3 y esa únicamente corre en el barrido diario.
            await PonerEstatusAlDia(resultado.SiniestroId!.Value, siniestro, ct);

            monitoreo.Rastrear("siniestros.guardado", $"Siniestro {siniestro.NumReporte} guardado",
                ("siniestro_id", resultado.SiniestroId?.ToString()),
                ("archivos", archivos.Count.ToString()));

            await SubirDocumentos(resultado.SiniestroId!.Value, siniestro.NumReporte, archivos, ct);

            log.LogInformation("Siniestro {NoReporte} procesado correctamente.", siniestro.NumReporte);
        }
        catch (Exception ex)
        {
            // No se re-lanza a propósito (mismo criterio que en Seguros): un siniestro con datos
            // malos en SICAS no debe abortar el barrido. Precisamente porque se silencia el flujo,
            // el evento tiene que llegar a Sentry o el registro se pierde sin dejar señal.
            monitoreo.Capturar(ex, "siniestros.procesar-siniestro",
                ("folio_siniestro", siniestro.NumReporte),
                ("iddocto", siniestro.IDDocto?.ToString()),
                ("consecuencia", "siniestro-omitido-lote-continua"));

            log.LogError(ex, "Error procesando siniestro {NoReporte}", siniestro.NumReporte);
            await RegistrarEnBitacora(
                $"Error procesando siniestro {siniestro.NumReporte}: {ex.Message}",
                NivelBitacora.Error, ct);
        }
    }

    private async Task SubirDocumentos(
        int siniestroId, string noReporte, List<ArchivoSICAS> archivos, CancellationToken ct)
    {
        foreach (var archivo in archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)))
        {
            try
            {
            string nombreBase = Path.GetFileNameWithoutExtension(archivo.NombreArchivo);
            if (await siniestroRepo.ExisteDocumentoAsync(siniestroId, nombreBase, ct))
                continue;

            _documentosIntentados++;

            var bytes = await sicasRestClient.DownloadFile(archivo.PathWWW!, ct);
            if (bytes is null || bytes.Length == 0)
            {
                log.LogWarning("No se pudo descargar el documento {Archivo} del siniestro {NoReporte}.",
                    archivo.NombreArchivo, noReporte);
                _documentosFallidos++;
                monitoreo.RastrearFallo("siniestros.subir-documentos",
                    $"No se pudo descargar de SICAS el documento {archivo.NombreArchivo}",
                    ("folio_siniestro", noReporte));
                continue;
            }

            string? nombreFinal = await documentService.SubirDocumentoSiniestro(archivo.NombreArchivo, bytes, ct);
            if (nombreFinal is null)
            {
                log.LogWarning("No se pudo subir el documento {Archivo} del siniestro {NoReporte} al FTP.",
                    archivo.NombreArchivo, noReporte);
                _documentosFallidos++;
                monitoreo.RastrearFallo("siniestros.subir-documentos",
                    $"No se pudo subir al FTP el documento {archivo.NombreArchivo}",
                    ("folio_siniestro", noReporte), ("archivo", archivo.NombreArchivo));
                continue;
            }

            await siniestroRepo.RegistrarDocumentoAsync(siniestroId, nombreFinal, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // El FTP lleva caído desde agosto de 2026: sin esto, el primer documento que falla
                // deja al siniestro sin los siguientes y, peor, aborta su procesamiento.
                _documentosFallidos++;
                monitoreo.Capturar(ex, "siniestros.subir-documentos",
                    ("folio_siniestro", noReporte),
                    ("archivo", archivo.NombreArchivo),
                    ("consecuencia", "documento-omitido-el-siniestro-continua"));
                log.LogError(ex, "Error subiendo el documento {Archivo} del siniestro {NoReporte}; se continúa.",
                    archivo.NombreArchivo, noReporte);
            }
        }
    }

    /// <summary>
    /// Emite UN evento con el saldo de documentos de toda la corrida, en vez de uno por documento.
    ///
    /// Si fallaron todos, el problema no es de un archivo sino del FTP, y entonces sí alerta: eso
    /// significa que ningún siniestro de hoy tiene su documentación en LumoSys. Si fallaron
    /// algunos, queda registrado para auditoría sin notificar. Y si no falló ninguno, no se emite
    /// nada: el silencio es la señal de que todo fue bien.
    /// </summary>
    private void ReportarResumenDocumentos()
    {
        if (_documentosFallidos == 0) return;

        string motivo = $"{_documentosFallidos} de {_documentosIntentados} documentos de siniestro no se pudieron almacenar";

        if (_documentosFallidos == _documentosIntentados)
            monitoreo.ReportarFalloCritico("siniestros.documentos",
                motivo,
                "ningun-siniestro-de-la-corrida-tiene-sus-documentos",
                ("documentos_fallidos", _documentosFallidos.ToString()),
                ("documentos_intentados", _documentosIntentados.ToString()));
        else
            monitoreo.ReportarFalloSilencioso("siniestros.documentos",
                motivo,
                "esos-siniestros-quedaron-sin-parte-de-su-documentacion",
                ("documentos_fallidos", _documentosFallidos.ToString()),
                ("documentos_intentados", _documentosIntentados.ToString()));

        log.LogWarning("Documentos de la corrida: {Fallidos} de {Intentados} no se almacenaron.",
            _documentosFallidos, _documentosIntentados);
    }

    private async Task ProcesarBitacoraDia(DateTime desde, DateTime hasta, CancellationToken ct)
    {
        try
        {
            int totalComentarios = 0;
            int ultimaPagina = 0;

            // Se cuenta en vez de reportar caso por caso: la bitácora del día trae comentarios de
            // todos los siniestros de SICAS y que la mayoría no esté en dbLumoSys es lo esperable
            // (no son de la flotilla propia). Lo que sí es señal de avería es que NINGUNO llegue a
            // vincularse, que es exactamente como se comportó el bug de Fase 2 del 04/08/2026.
            int candidatos = 0;
            int vinculados = 0;
            int sinIdSiniestro = 0;

            for (int pagina = 1; pagina <= 30; pagina++)
            {
                var comentarios = await sicasClient.BuscarBitacoraPorFecha(desde, hasta, pagina, ct);
                if (comentarios.Count == 0) break;

                ultimaPagina = pagina;
                totalComentarios += comentarios.Count;
                log.LogInformation("Fase 2 — Bitácora {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}, página {Pagina}: {Count} comentarios",
                    desde, hasta, pagina, comentarios.Count);

                foreach (var item in comentarios.Where(b => b.IsAutom == 0))
                {
                    try
                    {
                    candidatos++;

                    if (item.IDSiniestro is null)
                    {
                        sinIdSiniestro++;
                        continue;
                    }

                    // H03314011 no trae NumReporte (folio) — solo IDSiniestro, el id interno de
                    // SICAS que se guarda en SIN_FOLIO_SICAS al crear el siniestro (Fase 1).
                    int? siniestroId = await siniestroRepo.BuscarIdPorFolioSicas(item.IDSiniestro.Value, ct);
                    if (siniestroId is null) continue; // siniestro ajeno a la flotilla: normal

                    vinculados++;

                    if (string.IsNullOrWhiteSpace(item.Comentario)) continue;

                    DateTime fechaRegistro = DateTime.TryParse(item.FechaHora, out var fr) ? fr : DateTime.Now;

                    bool existe = await siniestroRepo.ExisteEstatusAsync(
                        siniestroId.Value, item.Comentario, fechaRegistro, ct);

                    if (!existe)
                    {
                        await siniestroRepo.UpsertEstatusAsync(new DatosEstatus
                        {
                            // H03314011 no trae un estatus formal (los comentarios son texto libre
                            // de reparación/entrega, no corresponden al catálogo TIPOS_ESTATUS
                            // SOLICITUD/DOCUMENTOS FALTANTES/EN TRAMITE/etc.) — se fija "EN TRAMITE"
                            // para todo comentario de bitácora mientras el siniestro sigue abierto.
                            Estatus       = "EN TRAMITE",
                            Comentarios   = item.Comentario,
                            FechaEvento   = null,
                            FechaRegistro = fechaRegistro,
                            IdUser        = item.IdUser ?? 3
                        }, siniestroId.Value, ct);
                    }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Un comentario con datos imposibles no puede dejar sin procesar a los
                        // demás del día: se anota y se sigue con el siguiente.
                        monitoreo.Capturar(ex, "siniestros.bitacora-fase2",
                            ("idsiniestro", item.IDSiniestro?.ToString()),
                            ("consecuencia", "comentario-omitido-la-fase-continua"));
                        log.LogError(ex, "Error guardando un comentario de bitácora (IDSiniestro {Id}); se continúa.",
                            item.IDSiniestro);
                    }
                }
            }

            log.LogInformation("Fase 2 — Bitácora {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}: {Count} comentarios en total",
                desde, hasta, totalComentarios);

            monitoreo.Rastrear("siniestros.bitacora", "Fase 2 finalizada",
                ("comentarios", totalComentarios.ToString()),
                ("candidatos", candidatos.ToString()),
                ("vinculados", vinculados.ToString()));

            // Hubo comentarios manuales que procesar y ni uno solo se pudo vincular a un siniestro
            // de dbLumoSys. Con volumen alto eso no es casualidad estadística: apunta a que la
            // vinculación por IDSiniestro/SIN_FOLIO_SICAS se rompió otra vez, y el síntoma visible
            // sería el mismo de entonces — el historial de estatus deja de crecer, sin ningún error.
            if (candidatos >= 20 && vinculados == 0)
            {
                // Critico: con este volumen no es casualidad estadistica, es que la vinculacion
                // por IDSiniestro se rompio otra vez. El sintoma visible seria que el historial de
                // estatus deja de crecer, sin ningun error, que es como paso inadvertido en agosto.
                monitoreo.ReportarFalloCritico("siniestros.bitacora-fase2",
                    $"Ninguno de los {candidatos} comentarios del rango se pudo vincular a un siniestro",
                    "historial-de-estatus-no-se-actualiza",
                    ("rango_desde", desde.ToString("dd/MM/yyyy")),
                    ("rango_hasta", hasta.ToString("dd/MM/yyyy")),
                    ("candidatos", candidatos.ToString()),
                    ("sin_idsiniestro", sinIdSiniestro.ToString()));
            }

            if (ultimaPagina == 30)
            {
                string msg = $"Bitácora de siniestros {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                             "(30,000 comentarios) — es posible que existan más comentarios sin procesar en este rango.";
                log.LogWarning(msg);
                await RegistrarEnBitacora(msg, NivelBitacora.Aviso, ct);
            }
        }
        catch (Exception ex)
        {
            // Silenciado deliberado: la Fase 2 (comentarios de bitácora) es complementaria y su
            // fallo no debe invalidar los siniestros ya guardados en la Fase 1. Se reporta porque
            // su consecuencia real —el historial de estatus deja de actualizarse— es invisible en
            // la base de datos: no falta un registro, falta que crezca.
            monitoreo.Capturar(ex, "siniestros.bitacora-fase2",
                ("rango_desde", desde.ToString("dd/MM/yyyy")),
                ("rango_hasta", hasta.ToString("dd/MM/yyyy")),
                ("consecuencia", "comentarios-de-bitacora-no-actualizados"));

            log.LogError(ex, "Error procesando bitácora del rango {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}.", desde, hasta);
        }
    }

    /// <summary>
    /// Guarda el siniestro y, si falla solo porque su póliza todavía no está en SEGUROS, la trae de
    /// SICAS y reintenta una vez.
    ///
    /// El orden de llegada no está garantizado: un siniestro puede entrar antes que su póliza, y
    /// entonces el guardado revienta con un error que parece definitivo sin serlo. Pasaba con
    /// 042619730 (póliza 5267524) y 441837 (L0000006490-0), que estuvieron semanas sin poder
    /// entrar; traer la póliza y reintentar los resolvió en segundos.
    /// </summary>
    private async Task<GuardarSiniestroResult> GuardarTrayendoPolizaSiFalta(
        GuardarSiniestroCommand cmd, string noReporte, CancellationToken ct)
    {
        try
        {
            return await guardarHandler.Handle(cmd, ct);
        }
        catch (PolizaNoRegistradaException ex)
        {
            log.LogWarning("Siniestro {NoReporte}: su póliza {Poliza} no está en SEGUROS; se trae de SICAS y se reintenta.",
                noReporte, ex.NumeroPoliza);

            monitoreo.Rastrear("siniestros.poliza-faltante",
                $"Se trae la póliza {ex.NumeroPoliza} para poder guardar {noReporte}",
                ("folio_siniestro", noReporte), ("poliza", ex.NumeroPoliza));

            await segurosHandler.Handle(new ProcesarLoteSeguroCommand { Poliza = ex.NumeroPoliza }, ct);

            // Un solo reintento: si la póliza tampoco está en SICAS, la excepción sube y el catch
            // general de ProcesarSiniestroCompleto la reporta como cualquier otro fallo.
            return await guardarHandler.Handle(cmd, ct);
        }
    }

    /// <summary>
    /// Deja el historial y el estatus visible de UN siniestro igual a lo que SICAS reporta.
    ///
    /// Se llama cada vez que el ETL toca un siniestro, venga del barrido por fechas o de un
    /// reproceso manual por folio, para que el resultado sea el mismo por cualquiera de las dos
    /// vías. Antes, reprocesar un folio solo refrescaba sus datos —póliza, montos, fechas— y
    /// dejaba el estatus como estuviera: 20220000209858 seguía EN TRAMITE después de procesarlo,
    /// con SICAS diciendo FECHA DE RESOLUCION.
    ///
    /// No lanza: un siniestro cuyo historial no se pueda traer igual quedó guardado, y que falle
    /// esto no puede tumbar el barrido.
    /// </summary>
    private async Task PonerEstatusAlDia(int siniestroId, SiniestroResumenSICAS siniestro, CancellationToken ct)
    {
        try
        {
            if (siniestro.IDSiniestro.HasValue)
                await SincronizarBitacora(siniestroId, siniestro.IDSiniestro.Value,
                    siniestro.NumReporte!, siniestro.Status_Txt, siniestro.EjecutNombre, ct);

            string? oficial = MapearEstatusSicas(siniestro.Status_Txt);
            if (oficial is null) return;

            if (await siniestroRepo.AlinearEstatusEfectivoAsync(siniestroId, oficial, ct))
                log.LogInformation("Siniestro {NoReporte}: estatus alineado a {Estatus} según SICAS.",
                    siniestro.NumReporte, oficial);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            monitoreo.Capturar(ex, "siniestros.estatus-al-dia",
                ("folio_siniestro", siniestro.NumReporte),
                ("consecuencia", "siniestro-guardado-con-estatus-sin-actualizar"));
            log.LogError(ex, "Error poniendo al día el estatus de {NoReporte}; el siniestro sí quedó guardado.",
                siniestro.NumReporte);
        }
    }

    /// <summary>
    /// Fase 3. Revisa en SICAS, uno por uno, todos los siniestros que LumoSys muestra abiertos y
    /// los pone al día.
    ///
    /// Es la pieza que faltaba para que el proceso se mantenga solo. El barrido de la Fase 1 filtra
    /// por <c>DatSiniestros.FCaptura</c>, la fecha de ALTA: un siniestro capturado hace meses y
    /// cerrado ayer no vuelve a aparecer nunca en el rango "ayer → hoy". La Fase 2 trae sus
    /// comentarios nuevos, pero los inserta como EN TRAMITE, así que un siniestro ya cerrado
    /// incluso se REABRE al recibir un comentario. Resultado: el desfase crecía unos seis folios
    /// por día y había que corregirlo a mano.
    ///
    /// Aplica dos reglas de negocio:
    /// <list type="number">
    /// <item>Manda SICAS: el estatus visible se alinea con el <c>Status_Txt</c> oficial.</item>
    /// <item>Lo que ya no está en SICAS se cierra, porque el ETL no volverá a tocarlo nunca.</item>
    /// </list>
    /// </summary>
    private async Task ReconciliarAbiertos(CancellationToken ct)
    {
        try
        {
            var abiertos = await siniestroRepo.ObtenerSiniestrosAbiertosAsync(ct);
            log.LogInformation("Fase 3 — Reconciliación: {Total} siniestros abiertos por revisar.", abiertos.Count);

            int alineados = 0, cerradosSinSicas = 0, sinCambio = 0, fallidos = 0;

            foreach (var abierto in abiertos)
            {
                try
                {
                    var enSicas = await LocalizarEnSicas(abierto, ct);

                    if (enSicas is null)
                    {
                        if (await CerrarPorqueYaNoEstaEnSicas(abierto, ct)) cerradosSinSicas++;
                        continue;
                    }

                    string? oficial = MapearEstatusSicas(enSicas.Status_Txt);
                    if (oficial is null) { sinCambio++; continue; }

                    // Si LumoSys ya muestra lo mismo que SICAS no hay nada que hacer, y sobre todo
                    // no hace falta pedir su bitácora: los comentarios del día ya los trajo la
                    // Fase 2. Saltar esa segunda consulta en los siniestros que están bien —la
                    // mayoría— es lo que mantiene la reconciliación en un tiempo razonable.
                    if (CoincideEstatus(abierto.EstatusActual, oficial)) { sinCambio++; continue; }

                    // Hay desalineación: se traen sus comentarios para que queden con el estatus
                    // que les toca, y después se fija el que se ve.
                    if (enSicas.IDSiniestro.HasValue)
                        await SincronizarBitacora(abierto.SiniestroId, enSicas.IDSiniestro.Value,
                            abierto.NoReporte, enSicas.Status_Txt, enSicas.EjecutNombre, ct);

                    if (await siniestroRepo.AlinearEstatusEfectivoAsync(abierto.SiniestroId, oficial, ct))
                    {
                        alineados++;
                        log.LogInformation("Fase 3 — {NoReporte}: estatus alineado a {Estatus} según SICAS (LumoSys mostraba {Antes}).",
                            abierto.NoReporte, oficial, abierto.EstatusActual);
                    }
                    else sinCambio++;

                    await Task.Delay(200, ct); // mismo criterio que la Fase 1: no saturar SICAS
                }
                catch (Exception ex)
                {
                    // Un siniestro problemático no debe cortar la reconciliación de los demás.
                    fallidos++;
                    monitoreo.Capturar(ex, "siniestros.reconciliacion",
                        ("folio_siniestro", abierto.NoReporte),
                        ("siniestro_id", abierto.SiniestroId.ToString()),
                        ("consecuencia", "este-siniestro-sigue-desfasado"));
                    log.LogError(ex, "Fase 3 — error reconciliando {NoReporte}", abierto.NoReporte);
                }
            }

            log.LogInformation("Fase 3 — Reconciliación terminada: {Alineados} alineado(s), {Cerrados} cerrado(s) por no existir en SICAS, {SinCambio} ya correcto(s), {Fallidos} con error.",
                alineados, cerradosSinSicas, sinCambio, fallidos);

            monitoreo.Rastrear("siniestros.reconciliacion", "Fase 3 finalizada",
                ("abiertos", abiertos.Count.ToString()),
                ("alineados", alineados.ToString()),
                ("cerrados_sin_sicas", cerradosSinSicas.ToString()),
                ("fallidos", fallidos.ToString()));
        }
        catch (Exception ex)
        {
            // Silenciado igual que la Fase 2: su fallo no debe invalidar lo ya guardado. Pero se
            // reporta porque la consecuencia -el desfase vuelve a crecer- no deja rastro en la BD.
            monitoreo.Capturar(ex, "siniestros.reconciliacion",
                ("consecuencia", "el-desfase-de-estatus-no-se-corrige"));
            log.LogError(ex, "Error en la reconciliación de siniestros abiertos.");
        }
    }

    /// <summary>
    /// Encuentra en SICAS el siniestro que corresponde a este registro, o null si ya no existe.
    ///
    /// Si el registro trae SIN_FOLIO_SICAS la búsqueda es directa. Si no lo trae -son capturas
    /// manuales previas a la migración- se busca por folio y se acepta únicamente un siniestro que
    /// no pertenezca ya a OTRO registro de dbLumoSys: de lo contrario dos copias del mismo folio
    /// se apropiarían del mismo siniestro y se pisarían la una a la otra.
    /// </summary>
    private async Task<SiniestroResumenSICAS?> LocalizarEnSicas(SiniestroAbierto abierto, CancellationToken ct)
    {
        if (abierto.FolioSicas.HasValue)
            return await sicasClient.BuscarPorIdSiniestro(abierto.FolioSicas.Value, ct);

        var candidatos = await sicasClient.BuscarPorReporte(abierto.NoReporte, ct);

        foreach (var candidato in candidatos.Where(c => c.IDSiniestro.HasValue))
        {
            int? dueno = await siniestroRepo.BuscarIdPorFolioSicas(candidato.IDSiniestro!.Value, ct);
            if (dueno is null || dueno == abierto.SiniestroId)
                return candidato;
        }

        return null;
    }

    /// <summary>
    /// Cierra un siniestro que ya no existe en SICAS.
    ///
    /// SICAS es la fuente de verdad: si el folio no está allí, el ETL no volverá a tocarlo nunca y
    /// se quedaría abierto para siempre, inflando el conteo de siniestros en trámite que ve
    /// operaciones. Se cierra en cuanto se detecta, sin esperar.
    ///
    /// El cambio es reversible y queda registrado: solo se reetiqueta el estatus del comentario
    /// vigente -no se borra ni se altera el historial- y cada cierre deja rastro en el log y en
    /// el monitoreo, con el SIN_ID y los días que llevaba sin movimiento.
    /// </summary>
    private async Task<bool> CerrarPorqueYaNoEstaEnSicas(SiniestroAbierto abierto, CancellationToken ct)
    {
        int antiguedad = (int)(DateTime.Now - abierto.UltimaActualizacion).TotalDays;

        bool cambio = await siniestroRepo.AlinearEstatusEfectivoAsync(
            abierto.SiniestroId, EstatusResolucion, ct);

        if (cambio)
        {
            log.LogInformation("Fase 3 — {NoReporte} (SIN_ID {Id}) se cierra: SICAS ya no lo tiene. Llevaba {Dias} día(s) sin movimiento.",
                abierto.NoReporte, abierto.SiniestroId, antiguedad);

            monitoreo.Rastrear("siniestros.reconciliacion",
                $"{abierto.NoReporte} cerrado por no existir en SICAS",
                ("folio_siniestro", abierto.NoReporte),
                ("siniestro_id", abierto.SiniestroId.ToString()),
                ("dias_sin_movimiento", antiguedad.ToString()));
        }

        return cambio;
    }

    /// <summary>
    /// Trae el historial de bitácora de un siniestro concreto y guarda lo que falte.
    ///
    /// El estatus se asigna en dos niveles, y la diferencia importa:
    /// el comentario MÁS RECIENTE lleva el <c>Status_Txt</c> que SICAS reporta para el siniestro,
    /// porque es el estatus oficial y es el que determina cómo se ve en LumoSys; los anteriores se
    /// quedan como EN TRAMITE, que es lo que eran mientras el siniestro seguía abierto.
    ///
    /// Esto corrige un error real: 1-211-2026-R-20949 tenía como último comentario "CERRADO: NA
    /// RESPONSABLE DE ATROPELLO CONTRA TERCERO…", donde ese "CERRADO" se refiere al cierre de la
    /// gestión con el tercero, no del siniestro. SICAS lo mantenía en SOLICITUD, pero deducir el
    /// estatus del texto lo daba por resuelto.
    /// </summary>
    private async Task SincronizarBitacora(
        int siniestroId, int idSiniestroSicas, string noReporte,
        string? estatusSicas, string? ejecutivoSicas, CancellationToken ct)
    {
        var comentarios = await sicasClient.BuscarBitacoraPorSiniestro(idSiniestroSicas, ct);

        var manuales = comentarios
            .Where(b => b.IsAutom == 0 && !string.IsNullOrWhiteSpace(b.Comentario))
            .OrderBy(b => FechaComentario(b.FechaHora))
            .ToList();

        if (manuales.Count == 0) return;

        int nuevos = 0, corregidos = 0;

        for (int i = 0; i < manuales.Count; i++)
        {
            try
            {
            var item = manuales[i];
            DateTime fechaRegistro = FechaComentario(item.FechaHora);
            bool esElMasReciente = i == manuales.Count - 1;

            string estatus = esElMasReciente
                ? (MapearEstatusSicas(estatusSicas) ?? EstatusTramite)
                : EstatusTramite;

            if (await siniestroRepo.ExisteEstatusAsync(siniestroId, item.Comentario!, fechaRegistro, ct))
            {
                // Ya está guardado, pero pudo haberse registrado con el EN TRAMITE fijo de la
                // Fase 2. Se reetiqueta sin reinsertarlo: es lo que corrige a los siniestros
                // cerrados que operaciones sigue viendo en trámite.
                corregidos += await siniestroRepo.CorregirTipoEstatusAsync(
                    siniestroId, item.Comentario!, fechaRegistro, estatus, ct);
                continue;
            }

            await siniestroRepo.UpsertEstatusAsync(new DatosEstatus
            {
                Estatus       = estatus,
                Comentarios   = item.Comentario,
                FechaEvento   = null,
                FechaRegistro = fechaRegistro,
                IdUser        = item.IdUser ?? 3,
                Ejecutivo     = ejecutivoSicas
            }, siniestroId, ct);
            nuevos++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                monitoreo.Capturar(ex, "siniestros.bitacora",
                    ("folio_siniestro", noReporte),
                    ("consecuencia", "comentario-omitido-el-siniestro-continua"));
                log.LogError(ex, "Error sincronizando un comentario de {NoReporte}; se continúa.", noReporte);
            }
        }

        if (nuevos > 0 || corregidos > 0)
            log.LogInformation("Siniestro {NoReporte}: bitácora sincronizada, {Nuevos} nuevo(s) y {Corregidos} reetiquetado(s) de {Total} en SICAS. Estatus oficial: {Estatus}",
                noReporte, nuevos, corregidos, comentarios.Count, estatusSicas ?? "(no informado)");
    }

    /// <summary>Fecha del comentario; si SICAS la manda vacía o en un formato inesperado se usa el
    /// momento actual, para no perder el registro.</summary>
    private static DateTime FechaComentario(string? fechaHora) =>
        DateTime.TryParse(fechaHora, out var f) ? f : DateTime.Now;

    private const string EstatusTramite    = "EN TRAMITE";
    private const string EstatusResolucion = "FECHA DE RESOLUCIÓN";

    /// <summary>
    /// Compara dos estatus del catálogo ignorando acentos y mayúsculas.
    ///
    /// Hace falta porque el mismo estatus viaja escrito de dos formas: SICAS manda
    /// "FECHA DE RESOLUCION" y el catálogo de LumoSys lo guarda como "FECHA DE RESOLUCIÓN".
    /// Comparándolos en crudo, un siniestro correcto se vería desalineado en cada corrida y la
    /// reconciliación lo reprocesaría eternamente.
    /// </summary>
    private static bool CoincideEstatus(string? a, string? b) =>
        string.Compare(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty,
            CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>
    /// Traduce el <c>Status_Txt</c> de SICAS al catálogo TIPOS_ESTATUS, o <c>null</c> si SICAS no lo
    /// informó o mandó algo que no está en el catálogo. Se devuelve null en vez de adivinar: quien
    /// alinea el estatus visible necesita distinguir "SICAS dice esto" de "no sabemos", y deducirlo
    /// del texto del comentario ya demostró dar falsos cierres.
    /// </summary>
    private static string? MapearEstatusSicas(string? statusSicas) =>
        (statusSicas ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "FECHA DE RESOLUCION" or "FECHA DE RESOLUCIÓN" => EstatusResolucion,
            "EN TRAMITE" or "EN TRÁMITE"                   => EstatusTramite,
            "CANCELACION" or "CANCELACIÓN"                 => "CANCELACIÓN",
            "SOLICITUD"                                    => "SOLICITUD",
            "DOCUMENTOS FALTANTES"                         => "DOCUMENTOS FALTANTES",
            "SATISFACCION DE CLIENTE"                      => "SATISFACCION DE CLIENTE",
            _                                              => null,
        };

    /// <summary>
    /// Deja constancia en la bitácora sin arriesgar el lote.
    ///
    /// Escribir en la bitácora es, a su vez, un acceso a dbLumoSys. Casi todas estas llamadas están
    /// dentro de un catch, es decir justo cuando algo ya va mal: si lo que falla es la propia base,
    /// la escritura lanzaría DESDE el catch y abortaría el barrido entero. Se pierde el renglón de
    /// bitácora, nunca el resto del proceso; el evento ya viajó a Sentry de todos modos.
    /// </summary>
    private async Task RegistrarEnBitacora(string mensaje, NivelBitacora nivel, CancellationToken ct)
    {
        try
        {
            await bitacora.GuardarAsync(mensaje, nivel, _idAplicacion, ct);
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "siniestros.bitacora-escritura", ("mensaje", mensaje));
            log.LogError(ex, "No se pudo registrar en la bitácora: {Mensaje}", mensaje);
        }
    }

    private static GuardarSiniestroCommand ConstruirComando(
        SiniestroResumenSICAS siniestro,
        PolizaDetalleSICASS detalle)
    {
        string? tipoSiniestro = ObtenerTipoSiniestro(siniestro.CobAfectada);
        bool esRobo = (tipoSiniestro ?? string.Empty).Contains("ROBO", StringComparison.OrdinalIgnoreCase);

        return new GuardarSiniestroCommand
        {
            Poliza            = siniestro.Documento,
            NoSerie           = detalle.Serie,
            TipoSiniestro     = tipoSiniestro,
            FechaEvento       = siniestro.FPSintoma,
            Descripcion       = siniestro.Descripcion,
            NoSiniestro       = siniestro.NumSiniestro,
            NoReporte         = siniestro.NumReporte,
            IDSiniestro       = siniestro.IDSiniestro,
            FechaResolucion   = siniestro.FStatus,
            MontoIndemnizable = esRobo ? 0 : null,
            MontoDeducible    = null,
            MontoPrimasPendientes = esRobo ? 0 : null,
            MontoOtrosDescuentos  = esRobo ? 0 : null,
            Inciso            = int.TryParse(siniestro.Inciso, out var inciso) ? inciso : null
        };
    }

    /// <summary>Replica ObtenerTipoSiniestro del ETL legacy: normaliza variantes de texto de
    /// CobAfectada antes de resolver contra el catálogo TIPOS_SINIESTROS.</summary>
    private static string? ObtenerTipoSiniestro(string? cobAfectada)
    {
        if (string.IsNullOrEmpty(cobAfectada))
            return null;

        string texto = cobAfectada.ToUpper();
        return texto switch
        {
            "DAÑO MATERIAL" or "DAÑOS MATERIALES" or "DM" => "DAÑOS MATERIALES (CHOQUE/REPARACION)",
            "ROTURA DE CRISTALES" or "ROTIRA DE CRISTALES" => "CRISTALES",
            "ASISTENCIA" => "ASISTENCIA VIAL",
            _ => cobAfectada
        };
    }
}
