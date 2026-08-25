using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Domain.Shared.Models;
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
    IBitacoraRepository bitacora,
    IOptions<AplicacionOptions> opciones,
    ILogger<ProcesarLoteSiniestroHandler> log)
{
    private readonly int _idAplicacion = opciones.Value.Siniestros;

    public async Task Handle(ProcesarLoteSiniestroCommand cmd, CancellationToken ct = default)
    {
        bool porFolio = !string.IsNullOrWhiteSpace(cmd.FolioSiniestro);
        string modo = porFolio ? "folio" : cmd.Desde.HasValue || cmd.Hasta.HasValue ? "rango" : "diario";

        var resumen = new ResumenLote("Siniestros", modo);

        // Scope de correlación: todas las líneas de esta corrida quedan marcadas con el mismo
        // id en el archivo, para poder aislarlas cuando corren varios barridos traslapados.
        using var _ = log.BeginScope(new Dictionary<string, object?>
        {
            ["lote"] = resumen.CorrelacionId,
            ["modo"] = modo
        });

        try
        {
            if (porFolio)
            {
                await ProcesarPorReporte(cmd.FolioSiniestro!, resumen, ct);

                // Aviso explícito: el reproceso por folio NO ejecuta la Fase 2, así que el
                // estatus y los comentarios del siniestro no se actualizan en esta pasada.
                // Para eso hace falta un barrido por rango de fechas.
                log.LogWarning("Reproceso por folio: NO se ejecuta la Fase 2 (bitacora). El estatus y " +
                               "los comentarios de {Folio} no se actualizan aqui; para eso corre un " +
                               "barrido por rango de fechas.", cmd.FolioSiniestro);
                return;
            }

            await ProcesarLote(cmd.Desde ?? DateTime.Now.AddDays(-1), cmd.Hasta ?? DateTime.Now, resumen, ct);
        }
        finally
        {
            // El resumen se emite siempre, incluso si el lote se cortó por cancelación o error:
            // saber hasta dónde llegó es justamente lo que se necesita en ese caso.
            if (resumen.TieneIncidencias)
                log.LogWarning("{Resumen}", resumen.Resumir());
            else
                log.LogInformation("{Resumen}", resumen.Resumir());
        }
    }

    private async Task ProcesarLote(DateTime desde, DateTime hasta, ResumenLote resumen, CancellationToken ct)
    {
        log.LogInformation("Iniciando lote Siniestros {Desde:dd/MM/yyyy} -> {Hasta:dd/MM/yyyy}", desde, hasta);

        int ultimaPagina = 0;
        for (int pagina = 1; pagina <= 30; pagina++)
        {
            var siniestros = await sicasClient.BuscarSiniestrosVigentes(desde, hasta, pagina, ct);
            if (siniestros.Count == 0) break;

            ultimaPagina = pagina;
            resumen.Inc(ResumenLote.Paginas);
            resumen.Inc(ResumenLote.Leidos, siniestros.Count);
            log.LogInformation("Pagina {Pagina}: {Count} siniestros", pagina, siniestros.Count);

            foreach (var siniestro in siniestros)
            {
                await ProcesarSiniestroCompleto(siniestro, resumen, ct);
                await Task.Delay(300, ct); // evita ráfagas hacia SICAS (throttling observado en Seguros)
            }
        }

        if (ultimaPagina == 30)
        {
            string msg = $"Barrido de siniestros {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                         "(3,000 registros) — es posible que existan más siniestros sin procesar en este rango.";
            log.LogWarning("{Mensaje}", msg);
            await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
        }

        // Fase 2: comentarios de bitácora del rango
        await ProcesarBitacoraDia(desde, hasta, resumen, ct);

        log.LogInformation("Lote Siniestros finalizado.");
    }

    private async Task ProcesarPorReporte(string noReporte, ResumenLote resumen, CancellationToken ct)
    {
        log.LogInformation("Procesando siniestro por folio: {NoReporte}", noReporte);

        var resultados = await sicasClient.BuscarPorReporte(noReporte, ct);
        if (resultados.Count == 0)
        {
            log.LogWarning("Siniestro {NoReporte} no encontrado en SICAS.", noReporte);
            await bitacora.GuardarAsync($"Siniestro {noReporte} no encontrado en SICAS.",
                NivelBitacora.Aviso, _idAplicacion, ct);
            return;
        }

        resumen.Inc(ResumenLote.Leidos, resultados.Count);

        // Solo se procesa el primero. Si SICAS devolvió varios (varias coberturas afectadas o
        // varios incisos del mismo folio) el resto se descarta: antes ocurría sin dejar rastro.
        if (resultados.Count > 1)
        {
            log.LogWarning("SICAS devolvio {Count} filas para el folio {NoReporte}; solo se procesa la " +
                           "primera (IDSiniestro={Primero}). Se descartan: {Otros}",
                resultados.Count, noReporte, resultados[0].IDSiniestro,
                string.Join(", ", resultados.Skip(1).Select(r => r.IDSiniestro)));
        }

        await ProcesarSiniestroCompleto(resultados[0], resumen, ct);
    }

    private async Task ProcesarSiniestroCompleto(
        SiniestroResumenSICAS siniestro, ResumenLote resumen, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siniestro.NumReporte))
        {
            log.LogWarning("Siniestro sin NumReporte (IDDocto={IDDocto}, IDSiniestro={IDSiniestro}); se omite.",
                siniestro.IDDocto, siniestro.IDSiniestro);
            resumen.Inc(ResumenLote.OmitidosSinDatos);
            return;
        }

        try
        {
            if (siniestro.IDDocto is null)
            {
                log.LogWarning("Siniestro {NoReporte}: sin IDDocto, no se puede resolver la serie.", siniestro.NumReporte);
                resumen.Inc(ResumenLote.OmitidosSinDatos);
                return;
            }

            // HDS00009 no trae la serie del vehículo — sale de una consulta aparte (HWS_DDETAIL),
            // igual que en Seguros (mismo patrón que el ETL legacy: PolizaDetalle es un objeto separado).
            var detalle = await sicasClient.BuscarDetalle(siniestro.IDDocto.Value, ct);
            if (detalle?.Serie is null)
            {
                log.LogWarning("Siniestro {NoReporte}: no se pudo resolver la serie del vehiculo (IDDocto={IDDocto}).",
                    siniestro.NumReporte, siniestro.IDDocto);
                resumen.Inc(ResumenLote.OmitidosSinDatos);
                return;
            }

            if (!await siniestroRepo.ExisteVehiculoAsync(detalle.Serie, ct))
            {
                log.LogInformation("Siniestro {NoReporte} (serie {Serie}) no pertenece a la flotilla propia; se omite.",
                    siniestro.NumReporte, detalle.Serie);
                resumen.Inc(ResumenLote.OmitidosFlotilla);
                return;
            }

            // No se obtiene bitácora aquí: SICAS solo expone el ClaveBit de un siniestro vía una
            // operación SOAP sin equivalente REST confirmado (WS_Siniestros + parseo de texto).
            // El historial de estatus se acumula de forma incremental vía ProcesarBitacoraDia
            // (Fase 2, H03314011 — sí funciona por REST y no depende de ClaveBit).
            var archivos = siniestro.IDSiniestro.HasValue
                ? await sicasClient.BuscarDigital(siniestro.IDSiniestro.Value, ct)
                : [];

            var cmd = ConstruirComando(siniestro, detalle);
            var resultado = await guardarHandler.Handle(cmd, ct);

            if (!resultado.Exitoso)
            {
                if (resultado.Omitido)
                {
                    log.LogInformation("Siniestro {NoReporte} omitido: {Mensaje}", siniestro.NumReporte, resultado.Mensaje);
                    resumen.Inc(ResumenLote.OmitidosFlotilla);
                    return;
                }

                log.LogError("Error guardando siniestro {NoReporte}: {Mensaje}",
                    siniestro.NumReporte, resultado.Mensaje);
                resumen.Inc(ResumenLote.Errores);
                await bitacora.GuardarAsync(
                    $"Error guardando siniestro {siniestro.NumReporte}: {resultado.Mensaje}",
                    NivelBitacora.Error, _idAplicacion, ct);
                return;
            }

            resumen.Inc(ResumenLote.Procesados);

            await SubirDocumentos(resultado.SiniestroId!.Value, siniestro.NumReporte, archivos, resumen, ct);

            log.LogInformation("Siniestro {NoReporte} procesado correctamente (SiniestroId={Id}, FolioSICAS={Folio}).",
                siniestro.NumReporte, resultado.SiniestroId, siniestro.IDSiniestro);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error procesando siniestro {NoReporte}", siniestro.NumReporte);
            resumen.Inc(ResumenLote.Errores);
            await bitacora.GuardarAsync(
                $"Error procesando siniestro {siniestro.NumReporte}: {ex.Message}",
                NivelBitacora.Error, _idAplicacion, ct);
        }
    }

    private async Task SubirDocumentos(
        int siniestroId, string noReporte, List<ArchivoSICAS> archivos, ResumenLote resumen, CancellationToken ct)
    {
        if (archivos.Count > 0)
            log.LogInformation("Siniestro {NoReporte}: SICAS devolvio {Count} archivo(s) digitales.",
                noReporte, archivos.Count);

        foreach (var archivo in archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)))
        {
            string nombreBase = Path.GetFileNameWithoutExtension(archivo.NombreArchivo);
            if (await siniestroRepo.ExisteDocumentoAsync(siniestroId, nombreBase, ct))
            {
                log.LogDebug("Documento {Archivo} ya registrado para el siniestro {NoReporte}; se omite.",
                    archivo.NombreArchivo, noReporte);
                continue;
            }

            var bytes = await sicasRestClient.DownloadFile(archivo.PathWWW!, ct);
            if (bytes is null || bytes.Length == 0)
            {
                log.LogWarning("No se pudo descargar el documento {Archivo} del siniestro {NoReporte}.",
                    archivo.NombreArchivo, noReporte);
                resumen.Inc(ResumenLote.DocsFallidos);
                continue;
            }

            string? nombreFinal = await documentService.SubirDocumentoSiniestro(archivo.NombreArchivo, bytes, ct);
            if (nombreFinal is null)
            {
                log.LogWarning("No se pudo subir el documento {Archivo} del siniestro {NoReporte} al FTP.",
                    archivo.NombreArchivo, noReporte);
                resumen.Inc(ResumenLote.DocsFallidos);
                continue;
            }

            await siniestroRepo.RegistrarDocumentoAsync(siniestroId, nombreFinal, ct);
            resumen.Inc(ResumenLote.DocsSubidos);
            log.LogInformation("Documento {Archivo} del siniestro {NoReporte} subido como {Final}.",
                archivo.NombreArchivo, noReporte, nombreFinal);
        }
    }

    private async Task ProcesarBitacoraDia(
        DateTime desde, DateTime hasta, ResumenLote resumen, CancellationToken ct)
    {
        try
        {
            int ultimaPagina = 0;
            var sinVinculo = new List<int>();

            for (int pagina = 1; pagina <= 30; pagina++)
            {
                var comentarios = await sicasClient.BuscarBitacoraPorFecha(desde, hasta, pagina, ct);
                if (comentarios.Count == 0) break;

                ultimaPagina = pagina;
                resumen.Inc(ResumenLote.ComentariosLeidos, comentarios.Count);
                log.LogInformation("Fase 2 - Bitacora {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}, pagina {Pagina}: {Count} comentarios",
                    desde, hasta, pagina, comentarios.Count);

                foreach (var item in comentarios.Where(b => b.IsAutom == 0))
                {
                    if (item.IDSiniestro is null)
                    {
                        log.LogWarning("Fase 2: comentario sin IDSiniestro (ClaveBit={Clave}); se descarta.", item.ClaveBit);
                        continue;
                    }

                    // H03314011 no trae NumReporte (folio) — solo IDSiniestro, el id interno de
                    // SICAS que se guarda en SIN_FOLIO_SICAS al crear el siniestro (Fase 1).
                    int? siniestroId = await siniestroRepo.BuscarIdPorFolioSicas(item.IDSiniestro.Value, ct);
                    if (siniestroId is null)
                    {
                        // ANTES ESTE CASO ERA UN `continue` MUDO. Es el defecto que mantuvo la
                        // bitácora vacía durante meses sin que nada lo delatara: SIN_FOLIO_SICAS
                        // solo empezó a poblarse el 04/08/2026, así que los siniestros anteriores
                        // (33,783 al 24/08/2026) no se pueden ligar y sus comentarios se perdían.
                        resumen.Inc(ResumenLote.ComentariosSinVinculo);
                        if (sinVinculo.Count < 50) sinVinculo.Add(item.IDSiniestro.Value);
                        log.LogWarning("Fase 2: comentario de IDSiniestro={IDSiniestro} no se pudo ligar (ningun " +
                                       "SINIESTROS.SIN_FOLIO_SICAS coincide). Comentario descartado: {Texto}",
                            item.IDSiniestro, Recortar(item.Comentario, 80));
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.Comentario))
                    {
                        log.LogDebug("Fase 2: comentario vacio para IDSiniestro={IDSiniestro}; se omite.", item.IDSiniestro);
                        continue;
                    }

                    DateTime fechaRegistro = DateTime.TryParse(item.FechaHora, out var fr) ? fr : DateTime.Now;

                    bool existe = await siniestroRepo.ExisteEstatusAsync(
                        siniestroId.Value, item.Comentario, fechaRegistro, ct);

                    if (existe)
                    {
                        resumen.Inc(ResumenLote.ComentariosYaExistian);
                        continue;
                    }

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

                    resumen.Inc(ResumenLote.ComentariosNuevos);
                    log.LogInformation("Fase 2: estatus nuevo para SiniestroId={Id} ({Fecha:dd/MM/yyyy HH:mm}): {Texto}",
                        siniestroId, fechaRegistro, Recortar(item.Comentario, 60));
                }
            }

            log.LogInformation("Fase 2 - Bitacora {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}: leidos={Leidos} " +
                               "nuevos={Nuevos} ya_existian={Existian} sin_vinculo={SinVinculo}",
                desde, hasta,
                resumen.Obtener(ResumenLote.ComentariosLeidos),
                resumen.Obtener(ResumenLote.ComentariosNuevos),
                resumen.Obtener(ResumenLote.ComentariosYaExistian),
                resumen.Obtener(ResumenLote.ComentariosSinVinculo));

            // Sube a la bitácora persistente: si esto aparece, hay siniestros que nunca van a
            // recibir sus comentarios hasta que se les rellene SIN_FOLIO_SICAS reprocesándolos.
            if (sinVinculo.Count > 0)
            {
                string msg = $"Fase 2 ({desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy}): " +
                             $"{resumen.Obtener(ResumenLote.ComentariosSinVinculo)} comentario(s) de SICAS no se " +
                             "pudieron ligar a ningun siniestro porque SIN_FOLIO_SICAS esta NULL. " +
                             $"IDSiniestro afectados (hasta 50): {string.Join(", ", sinVinculo)}. " +
                             "Reprocesar esos siniestros (por folio o por rango de FCaptura) rellena el campo.";
                log.LogWarning("{Mensaje}", msg);
                await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
            }

            if (ultimaPagina == 30)
            {
                string msg = $"Bitácora de siniestros {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                             "(30,000 comentarios) — es posible que existan más comentarios sin procesar en este rango.";
                log.LogWarning("{Mensaje}", msg);
                await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error procesando bitacora del rango {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}.", desde, hasta);
            resumen.Inc(ResumenLote.Errores);
        }
    }

    private static string Recortar(string? texto, int max) =>
        string.IsNullOrEmpty(texto) ? "" : texto.Length <= max ? texto : texto[..max] + "...";

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
