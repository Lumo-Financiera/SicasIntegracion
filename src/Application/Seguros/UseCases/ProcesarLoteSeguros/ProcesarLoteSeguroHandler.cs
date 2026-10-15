using System.Text.RegularExpressions;
using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;
using LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;

public sealed class ProcesarLoteSeguroHandler(
    ISeguroSICASClient sicasClient,
    ISICASRestClient sicasRestClient,
    ISFleetClient sfleetClient,
    IPolizaRepository polizaRepo,
    GuardarPolizaHandler guardarHandler,
    SubirDocumentoPolizaHandler subirHandler,
    IBitacoraRepository bitacora,
    IMonitoreoErrores monitoreo,
    IOptions<AplicacionOptions> opciones,
    ILogger<ProcesarLoteSeguroHandler> log)
{
    private readonly int _idAplicacion = opciones.Value.Seguros;

    // Saldo de la corrida para los dos servicios externos que fallan de forma repetida. Se reporta
    // una vez al final, con el conteo, en lugar de una vez por documento o por póliza: el reporte
    // individual producía cientos de eventos idénticos por noche sobre problemas ya conocidos.
    private int _documentosIntentados;
    private int _documentosFallidos;
    private int _sfleetIntentos;
    private int _sfleetFallos;
    private int _sfleetDocsIntentados;
    private int _sfleetDocsFallidos;
    private int _sfleetVehiculosBuscados;
    private int _sfleetVehiculosNoEncontrados;

    public async Task Handle(ProcesarLoteSeguroCommand cmd, CancellationToken ct = default)
    {
        // El disparador queda etiquetado desde el primer momento: en Sentry es la diferencia
        // entre "falló el barrido automático" y "falló un reprocesamiento que alguien pidió a
        // mano por la API", que se atienden de forma distinta.
        monitoreo.Etiquetar("modulo", "Seguros");

        if (!string.IsNullOrWhiteSpace(cmd.Poliza))
        {
            monitoreo.Etiquetar("disparador", "manual-poliza");
            await ProcesarPorDocumento(cmd.Poliza, ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(cmd.Serie))
        {
            monitoreo.Etiquetar("disparador", "manual-serie");
            await ProcesarPorSerie(cmd.Serie, ct);
            return;
        }

        await ProcesarLote(cmd.Desde ?? DateTime.Now.AddDays(-1), cmd.Hasta ?? DateTime.Now, ct);
    }

    private async Task ProcesarLote(DateTime desde, DateTime hasta, CancellationToken ct)
    {
        log.LogInformation("Iniciando lote Seguros {Desde:dd/MM/yyyy} → {Hasta:dd/MM/yyyy}", desde, hasta);

        // Cubre el lote pedido por la API (el disparado por los BackgroundServices ya trae estas
        // etiquetas desde la corrida; volver a fijarlas es idempotente).
        monitoreo.Etiquetar("rango_desde", desde.ToString("dd/MM/yyyy HH:mm"));
        monitoreo.Etiquetar("rango_hasta", hasta.ToString("dd/MM/yyyy HH:mm"));

        int ultimaPagina = 0;
        int totalRegistros = 0;
        for (int pagina = 1; pagina <= 30; pagina++)
        {
            List<PolizaResumenSICAS> polizas;
            try
            {
                polizas = await sicasClient.BuscarPolizasVigentes(desde, hasta, pagina, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Mismo criterio que en Siniestros: una pagina que falle no se lleva el resto.
                monitoreo.Capturar(ex, "seguros.barrido",
                    ("pagina", pagina.ToString()), ("consecuencia", "pagina-omitida-el-lote-continua"));
                log.LogError(ex, "Error leyendo la página {Pagina} del barrido de pólizas; se continúa.", pagina);
                continue;
            }

            if (polizas.Count == 0) break;

            ultimaPagina = pagina;
            totalRegistros += polizas.Count;
            log.LogInformation("Página {Pagina}: {Count} pólizas", pagina, polizas.Count);
            monitoreo.Rastrear("seguros.barrido", $"Página {pagina}: {polizas.Count} pólizas",
                ("pagina", pagina.ToString()), ("registros", polizas.Count.ToString()));

            foreach (var poliza in polizas)
            {
                if (poliza.IDDocto is null)
                {
                    // Se saltaba sin dejar constancia: la póliza no se procesa nunca y el lote
                    // termina declarándose completo.
                    monitoreo.ReportarFalloSilencioso("seguros.procesar-lote",
                        "SICAS devolvió una póliza sin IDDocto en el listado",
                        "poliza-omitida-sin-procesar",
                        ("poliza", poliza.Documento), ("pagina", pagina.ToString()));
                    continue;
                }
                await ProcesarPolizaCompleta(poliza, ct);
                await Task.Delay(300, ct); // evita ráfagas hacia SICAS (throttling observado en pruebas)
            }
        }

        if (ultimaPagina == 30)
        {
            string msg = $"Barrido de pólizas {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                         "(3,000 registros) — es posible que existan más pólizas sin procesar en este rango.";
            log.LogWarning(msg);
            await RegistrarEnBitacora(msg, NivelBitacora.Aviso, ct);
        }

        // Queda como etiqueta de la corrida para poder alertar en Sentry sobre barridos que
        // terminan "bien" con cero registros: ese fue el síntoma de los dos bugs de filtros de
        // fecha, invisible durante semanas porque un lote vacío no falla.
        monitoreo.Etiquetar("registros_encontrados", totalRegistros.ToString());

        ReportarResumenExterno();

        log.LogInformation("Lote Seguros finalizado. {Total} pólizas encontradas en el rango.", totalRegistros);
    }

    private async Task ProcesarPorSerie(string serie, CancellationToken ct)
    {
        log.LogInformation("Procesando póliza por serie: {Serie}", serie);

        var detalle = await sicasClient.BuscarDetallePorSerie(serie, ct);
        if (detalle?.IDDocto is null)
        {
            log.LogWarning("Serie {Serie} no encontrada en SICAS.", serie);
            await RegistrarEnBitacora($"Serie {serie} no encontrada en SICAS.", NivelBitacora.Aviso, ct);
            return;
        }

        var resumen = await sicasClient.BuscarPolizaPorIdDocto(detalle.IDDocto.Value, ct);
        if (resumen is null)
        {
            log.LogWarning("Póliza IDDocto={IDDocto} no encontrada como vigente.", detalle.IDDocto);
            return;
        }

        await ProcesarPolizaCompleta(resumen, ct);
    }

    private async Task ProcesarPorDocumento(string documento, CancellationToken ct)
    {
        log.LogInformation("Procesando póliza por folio: {Documento}", documento);

        int? idDocto = await sicasClient.BuscarIdDoctoporDocumento(documento, ct);
        if (idDocto is null)
        {
            log.LogWarning("Póliza {Documento} no encontrada en SICAS.", documento);
            await RegistrarEnBitacora($"Póliza {documento} no encontrada en SICAS.", NivelBitacora.Aviso, ct);
            return;
        }

        var resumen = await sicasClient.BuscarPolizaPorIdDocto(idDocto.Value, ct);
        if (resumen is null)
        {
            log.LogWarning("Póliza {Documento} (IDDocto={IDDocto}) no activa en H03117.", documento, idDocto);
            return;
        }

        await ProcesarPolizaCompleta(resumen, ct);
    }

    /// <summary>
    /// Emite el saldo de los servicios externos de toda la corrida, en vez de un evento por cada
    /// documento y cada póliza.
    ///
    /// Cuando falla el 100% el problema no es de un registro sino del servicio, y entonces sí
    /// alerta: significa que ninguna póliza de hoy llegó a SFleet, o que ningún documento se
    /// almacenó. Un fallo parcial queda registrado sin notificar, y si no hubo fallos no se emite
    /// nada.
    /// </summary>
    private void ReportarResumenExterno()
    {
        if (_documentosFallidos > 0)
        {
            string motivo = $"{_documentosFallidos} de {_documentosIntentados} documentos de póliza no se pudieron almacenar";

            if (_documentosFallidos == _documentosIntentados)
                monitoreo.ReportarFalloCritico("seguros.documentos", motivo,
                    "ninguna-poliza-de-la-corrida-tiene-sus-documentos",
                    ("documentos_fallidos", _documentosFallidos.ToString()),
                    ("documentos_intentados", _documentosIntentados.ToString()));
            else
                monitoreo.ReportarFalloSilencioso("seguros.documentos", motivo,
                    "esas-polizas-quedaron-sin-parte-de-su-documentacion",
                    ("documentos_fallidos", _documentosFallidos.ToString()),
                    ("documentos_intentados", _documentosIntentados.ToString()));

            log.LogWarning("Documentos de la corrida: {Fallidos} de {Intentados} no se almacenaron.",
                _documentosFallidos, _documentosIntentados);
        }

        // Si NINGUNA serie de la corrida se encontró en SFleet, el problema no son las series.
        // Este caso quedaba completamente mudo: `SincronizarSFleet` retorna antes de incrementar
        // los contadores de póliza, así que el resumen de abajo veía 0 intentos y 0 fallos y no
        // emitía nada. Con SFleet devolviendo 401 a todo —como se comprobó el 13/10/2026 contra
        // la API real— el ETL se declaraba exitoso sin haber sincronizado una sola póliza.
        const int MinimoParaSospechar = 5;
        if (_sfleetVehiculosBuscados >= MinimoParaSospechar
            && _sfleetVehiculosNoEncontrados == _sfleetVehiculosBuscados)
        {
            monitoreo.ReportarFalloCritico("sfleet.buscar-vehiculo",
                $"Ninguna de las {_sfleetVehiculosBuscados} series de la corrida se encontró en SFleet",
                "ninguna-poliza-llego-a-sfleet-probable-fallo-de-acceso-no-series-inexistentes",
                ("series_buscadas", _sfleetVehiculosBuscados.ToString()));

            log.LogError("SFleet: ninguna de las {N} series de la corrida se encontró. Se sospecha fallo de acceso (401/403) y no series inexistentes.",
                _sfleetVehiculosBuscados);
        }

        if (_sfleetDocsFallidos > 0)
        {
            string motivo = $"{_sfleetDocsFallidos} de {_sfleetDocsIntentados} documentos de póliza no se adjuntaron en SFleet";

            if (_sfleetDocsFallidos == _sfleetDocsIntentados)
                monitoreo.ReportarFalloCritico("seguros.sfleet-documentos", motivo,
                    "ningun-documento-de-la-corrida-llego-a-sfleet",
                    ("sfleet_docs_fallidos", _sfleetDocsFallidos.ToString()),
                    ("sfleet_docs_intentados", _sfleetDocsIntentados.ToString()));
            else
                monitoreo.ReportarFalloSilencioso("seguros.sfleet-documentos", motivo,
                    "esas-polizas-estan-en-sfleet-sin-parte-de-su-documentacion",
                    ("sfleet_docs_fallidos", _sfleetDocsFallidos.ToString()),
                    ("sfleet_docs_intentados", _sfleetDocsIntentados.ToString()));

            log.LogWarning("Documentos en SFleet: {Fallidos} de {Intentados} no se adjuntaron.",
                _sfleetDocsFallidos, _sfleetDocsIntentados);
        }

        if (_sfleetFallos > 0)
        {
            string motivo = $"{_sfleetFallos} de {_sfleetIntentos} pólizas no se pudieron sincronizar con SFleet";

            if (_sfleetFallos == _sfleetIntentos)
                monitoreo.ReportarFalloCritico("seguros.sfleet", motivo,
                    "ninguna-poliza-de-la-corrida-llego-a-sfleet",
                    ("sfleet_fallos", _sfleetFallos.ToString()),
                    ("sfleet_intentos", _sfleetIntentos.ToString()));
            else
                monitoreo.ReportarFalloSilencioso("seguros.sfleet", motivo,
                    "esas-polizas-viven-solo-en-lumosys",
                    ("sfleet_fallos", _sfleetFallos.ToString()),
                    ("sfleet_intentos", _sfleetIntentos.ToString()));

            log.LogWarning("SFleet en la corrida: {Fallos} de {Intentos} pólizas no se sincronizaron.",
                _sfleetFallos, _sfleetIntentos);
        }
    }

    private async Task ProcesarPolizaCompleta(PolizaResumenSICAS resumen, CancellationToken ct)
    {
        int idDocto = resumen.IDDocto!.Value;

        // Un ámbito por póliza: el contexto (IDDocto, folio) viaja con cualquier error que ocurra
        // más abajo — incluidos los que reportan los clientes de SICAS/FTP — y se descarta al
        // pasar a la siguiente póliza, sin arrastrar datos de la anterior.
        using var ambito = monitoreo.IniciarAmbito("seguros.procesar-poliza",
            ("iddocto", idDocto.ToString()),
            ("poliza", resumen.Documento));

        try
        {
            var detalle = await sicasClient.BuscarDetalle(idDocto, ct);
            if (detalle is null)
            {
                log.LogWarning("IDDocto={IDDocto}: faltan datos de detalle.", idDocto);

                // Sin detalle no hay serie, así que la póliza no se guarda. Puede ser un dato
                // incompleto en SICAS o un fallo de la consulta HWS_DDETAIL — desde aquí no se
                // distingue, y por eso hay que verlo: la póliza se pierde en ambos casos.
                monitoreo.ReportarFalloSilencioso("seguros.procesar-poliza",
                    "HWS_DDETAIL no devolvió el detalle del vehículo",
                    "poliza-no-guardada",
                    ("iddocto", idDocto.ToString()), ("poliza", resumen.Documento));
                return;
            }

            // Filtra antes de gastar más llamadas a SICAS: si la serie no es un vehículo propio,
            // no hay nada que guardar (evita registros huérfanos/duplicados, ver ExisteVehiculoAsync).
            if (!await polizaRepo.ExisteVehiculoAsync(detalle.Serie ?? string.Empty, ct))
            {
                log.LogInformation("Póliza {Poliza} (serie {Serie}) no pertenece a la flotilla propia; se omite.",
                    resumen.Documento, detalle.Serie);
                return;
            }

            var primas    = await sicasClient.BuscarPrimas(idDocto, ct);
            var coberturas = await sicasClient.BuscarCoberturas(idDocto, ct);
            var cobranza  = await sicasClient.BuscarCobranza(idDocto, ct);
            var archivos  = await sicasClient.BuscarDigital(idDocto, ct);

            if (primas is null)
            {
                log.LogWarning("IDDocto={IDDocto}: faltan datos de primas.", idDocto);

                // Llega aquí habiendo confirmado ya que la serie SÍ es de la flotilla propia:
                // es una póliza que nos corresponde y que se queda fuera de dbLumoSys.
                monitoreo.ReportarFalloSilencioso("seguros.procesar-poliza",
                    "H03400 no devolvió las primas de una póliza de la flotilla propia",
                    "poliza-no-guardada",
                    ("iddocto", idDocto.ToString()),
                    ("poliza", resumen.Documento),
                    ("serie", detalle.Serie));
                return;
            }

            monitoreo.Etiquetar("serie", detalle.Serie);

            var cmd = ConstruirComando(resumen, detalle, primas, coberturas, cobranza);
            var resultado = await guardarHandler.Handle(cmd, ct);

            if (!resultado.Exitoso)
            {
                if (resultado.Omitido)
                {
                    log.LogInformation("Póliza {Poliza} omitida: {Mensaje}", resumen.Documento, resultado.Mensaje);
                    return;
                }

                log.LogError("Error guardando póliza {Poliza}: {Mensaje}", resumen.Documento, resultado.Mensaje);
                await RegistrarEnBitacora($"Error guardando póliza {resumen.Documento}: {resultado.Mensaje}",
                    NivelBitacora.Error, ct);
                return;
            }

            monitoreo.Rastrear("seguros.guardado", $"Póliza {resumen.Documento} guardada",
                ("poliza_id", resultado.PolizaId?.ToString()),
                ("vehiculo_id", resultado.VehiculoId?.ToString()));

            // Subir documentos directamente desde SICAS (sin disco local)
            log.LogInformation("IDDocto={IDDocto}: SICAS devolvió {Count} archivo(s) digitales.", idDocto, archivos.Count);

            // Los bytes descargados se conservan para reutilizarlos en SFleet: son los mismos
            // documentos y volver a pedírselos a SICAS duplicaría el tráfico sin ganar nada.
            var descargados = new List<DocumentoDescargado>();

            // El contenido se pide en bloque para toda la póliza: si la descarga directa no está
            // disponible —en producción el host de los documentos no resuelve— el respaldo por API
            // trae todos los archivos de la entidad en una sola respuesta, y pedirlo archivo por
            // archivo multiplicaría ese costo.
            var pendientes = archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)).ToList();
            var contenidos = await sicasRestClient.ObtenerContenidos("H02", idDocto, pendientes, ct);

            foreach (var archivo in pendientes)
            {
                try
                {
                _documentosIntentados++;

                contenidos.TryGetValue(archivo.NombreArchivo, out var bytes);
                if (bytes is null || bytes.Length == 0)
                {
                    log.LogWarning("No se pudo descargar el documento {Archivo} de la póliza {Poliza}.",
                        archivo.NombreArchivo, resumen.Documento);

                    // Rastro y no evento: el saldo de toda la corrida se reporta una sola vez al
                    // final. DownloadFile ya dejó la causa técnica registrada.
                    _documentosFallidos++;
                    monitoreo.RastrearFallo("seguros.subir-documentos",
                        $"No se pudo descargar de SICAS el documento {archivo.NombreArchivo}",
                        ("poliza", resumen.Documento), ("serie", detalle.Serie));
                    continue;
                }

                descargados.Add(new DocumentoDescargado(archivo.NombreArchivo, bytes));

                await subirHandler.Handle(new SubirDocumentoPolizaCommand
                {
                    Serie         = detalle.Serie ?? string.Empty,
                    NombreArchivo = archivo.NombreArchivo,
                    Bytes         = bytes
                }, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // El FTP lleva caído desde agosto de 2026: sin esto, el primer documento que
                    // falla deja a la póliza sin los siguientes y aborta su procesamiento, que
                    // incluye la sincronización con SFleet.
                    _documentosFallidos++;
                    monitoreo.Capturar(ex, "seguros.subir-documentos",
                        ("poliza", resumen.Documento),
                        ("archivo", archivo.NombreArchivo),
                        ("consecuencia", "documento-omitido-la-poliza-continua"));
                    log.LogError(ex, "Error subiendo el documento {Archivo} de la póliza {Poliza}; se continúa.",
                        archivo.NombreArchivo, resumen.Documento);
                }
            }

            // Sincronizar con SFleet
            if (resultado.VehiculoId.HasValue)
                await SincronizarSFleet(cmd, resultado.VehiculoId.Value, descargados, ct);

            log.LogInformation("Póliza {Poliza} ({Serie}) procesada correctamente.",
                resumen.Documento, detalle.Serie);
        }
        catch (Exception ex)
        {
            // No se re-lanza a propósito, y esa es la regla de negocio: una póliza con datos malos
            // en SICAS no debe abortar el resto del barrido. Justo por eso hay que reportarla —
            // de lo contrario el lote termina "sin errores" con registros silenciosamente perdidos.
            monitoreo.Capturar(ex, "seguros.procesar-poliza",
                ("iddocto", idDocto.ToString()),
                ("poliza", resumen.Documento),
                ("consecuencia", "poliza-omitida-lote-continua"));

            log.LogError(ex, "Error procesando IDDocto={IDDocto}", idDocto);
            await RegistrarEnBitacora($"Error procesando póliza IDDocto={idDocto}: {ex.Message}",
                NivelBitacora.Error, ct);
        }
    }

    /// <summary>
    /// Sincroniza la póliza con SFleet y le adjunta sus documentos digitales.
    ///
    /// La subida de documentos existía en el ETL legacy (<c>GuardarSeguroDocumento</c>, un foreach
    /// sobre los documentos del vehículo justo después del alta) y se perdió al reescribir el
    /// integrador: el método <c>ISFleetClient.SubirDocumento</c> se portó completo pero nunca se
    /// llegó a llamar, y esta función recibía la lista de archivos solo para ignorarla. Resultado:
    /// desde julio de 2026 ningún documento de SICAS llegó jamás a SFleet.
    ///
    /// El orden importa: primero el alta/actualización de la póliza, porque el documento se cuelga
    /// de ella (<c>serviable_id</c>). Si la póliza no se pudo guardar no hay a qué adjuntarlo y los
    /// documentos se omiten en vez de subirse sueltos.
    /// </summary>
    private async Task SincronizarSFleet(
        GuardarPolizaCommand cmd,
        int vehiculoId,
        List<DocumentoDescargado> documentos,
        CancellationToken ct)
    {
        string numeroPoliza = cmd.Poliza;
        string serie = cmd.Vehiculo?.Serie ?? string.Empty;

        try
        {
            _sfleetVehiculosBuscados++;

            int? vehiculoSFleet = await sfleetClient.BuscarVehiculo(serie, ct);
            if (vehiculoSFleet is null)
            {
                // Se cuenta aparte porque aquí se confunden dos cosas muy distintas: que ESA serie
                // no esté dada de alta en SFleet (rutinario) y que SFleet no conteste a nadie. La
                // diferencia solo se ve en el agregado — ver ReportarResumenExterno.
                _sfleetVehiculosNoEncontrados++;
                log.LogWarning("Serie {Serie} no encontrada en SFleet.", serie);
                return;
            }

            int? polizaSFleetId = await sfleetClient.BuscarPoliza(numeroPoliza, ct);

            var vehiculo = cmd.Vehiculo;
            var solicitud = new LumoSys.Integraciones.Domain.Seguros.Models.SolicitudSFleet
            {
                NumeroPoliza           = numeroPoliza,
                ClienteVehiculoId      = vehiculoSFleet.Value,
                Aseguradora            = cmd.Aseguradora,
                FechaInicio            = DateTime.TryParse(vehiculo?.FechaInicio, out var fi) ? fi : default,
                FechaVencimiento       = DateTime.TryParse(vehiculo?.FechaVencimiento, out var fv) ? fv : default,
                Beneficiario           = cmd.Beneficiario,
                BeneficiarioPreferente = cmd.BeneficiarioPreferente,
                Broker                 = cmd.Broker,
                Ejecutivo              = vehiculo?.Ejecutivo,
                AdministracionCartera  = vehiculo?.AdministracionCartera,
                Cobertura              = vehiculo?.Cobertura,
                TipoPoliza             = vehiculo?.TipoPoliza,
                TipoUso                = vehiculo?.TipoUso,
                Pasajeros              = vehiculo?.Pasajeros,
                Inciso                 = vehiculo?.Inciso,
                PrimaNeta              = vehiculo?.PrimaNeta ?? 0,
                PrimaTotal             = vehiculo?.PrimaTotal ?? 0,
                Derechos               = vehiculo?.Derechos,
                Recargos               = vehiculo?.Recargos,
                IVA                    = vehiculo?.IVA,
                PrimerRecibo           = vehiculo?.PrimerRecibo,
                Subsecuente            = vehiculo?.Subsecuente,
                FormaPago              = cmd.FormaPago,
                GestionPago            = vehiculo?.GestionPago,
                Adaptacion             = vehiculo?.Adaptacion,
                ValorAdaptacion        = vehiculo?.ValorAdaptacion,
                ValorFactura           = vehiculo?.ValorFactura,
                // SICAS no entrega este dato y el repositorio guarda SDE_AMPARO_RC_ADICIONALES
                // como null a propósito. Se manda vacío en vez de rellenarlo con otra cobertura:
                // un campo sin dato es preferible a un dato que significa otra cosa.
                ResponsabilidadCivilAdicionales = null
            };

            // Se manda el id de la PÓLIZA, no el del vehículo: ese ya viaja en ClienteVehiculoId.
            _sfleetIntentos++;
            int idEnSFleet = await sfleetClient.GuardarPoliza(solicitud, polizaSFleetId, ct);

            if (idEnSFleet == 0)
            {
                _sfleetFallos++;

                if (documentos.Count > 0)
                {
                    // Sin póliza en SFleet no hay serviable_id al que colgar los documentos. Se
                    // deja constancia para que el saldo de la corrida no se lea como "sin
                    // documentos que subir" cuando en realidad sí los había.
                    _sfleetDocsIntentados += documentos.Count;
                    _sfleetDocsFallidos   += documentos.Count;
                    monitoreo.RastrearFallo("seguros.sfleet-documentos",
                        $"{documentos.Count} documento(s) no se adjuntaron: la póliza no quedó en SFleet",
                        ("poliza", numeroPoliza), ("serie", serie));
                }

                return;
            }

            await AdjuntarDocumentosSFleet(idEnSFleet, vehiculoSFleet.Value, numeroPoliza, serie, documentos, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paro del servicio: la póliza ya quedó guardada en dbLumoSys y la próxima corrida la
            // vuelve a sincronizar. Sin este filtro, cada despliegue dejaría eventos falsos.
            log.LogInformation("Sincronización con SFleet de {Poliza} interrumpida por paro del servicio.", numeroPoliza);
        }
        catch (Exception ex)
        {
            // Silenciado deliberado: la póliza ya quedó guardada en dbLumoSys y el ETL no debe
            // deshacerla porque SFleet (sistema aparte) esté caído o rechace la petición. Se
            // reporta con la marca de que quedó desincronizada, que es la acción pendiente real.
            monitoreo.Capturar(ex, "seguros.sincronizar-sfleet",
                ("poliza", numeroPoliza),
                ("serie", serie),
                ("vehiculo_id", vehiculoId.ToString()),
                ("consecuencia", "guardada-en-lumosys-sin-sincronizar-sfleet"));

            log.LogError(ex, "Error sincronizando SFleet para póliza {Poliza}", numeroPoliza);
        }
    }

    /// <summary>
    /// Adjunta a la póliza de SFleet los documentos que ya se descargaron de SICAS.
    ///
    /// Cada documento se aísla: que uno falle no debe dejar sin subir a los que siguen ni tumbar
    /// la sincronización de la póliza, que a estas alturas ya quedó bien guardada.
    /// </summary>
    private async Task AdjuntarDocumentosSFleet(
        int polizaSFleetId,
        int vehiculoSFleetId,
        string numeroPoliza,
        string serie,
        List<DocumentoDescargado> documentos,
        CancellationToken ct)
    {
        foreach (var documento in documentos)
        {
            _sfleetDocsIntentados++;

            try
            {
                bool subido = await sfleetClient.SubirDocumento(
                    polizaSFleetId, vehiculoSFleetId, documento.Nombre, documento.Bytes, ct);

                if (!subido)
                {
                    // SubirDocumento ya reportó el motivo técnico; aquí solo se cuenta para el
                    // saldo de la corrida.
                    _sfleetDocsFallidos++;
                    continue;
                }

                monitoreo.Rastrear("sfleet.documento-adjuntado",
                    $"{documento.Nombre} adjuntado a la póliza {numeroPoliza}",
                    ("poliza_sfleet_id", polizaSFleetId.ToString()),
                    ("bytes", documento.Bytes.Length.ToString()));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _sfleetDocsFallidos++;
                monitoreo.Capturar(ex, "seguros.sfleet-documentos",
                    ("poliza", numeroPoliza),
                    ("serie", serie),
                    ("archivo", documento.Nombre),
                    ("consecuencia", "documento-no-adjuntado-en-sfleet-la-poliza-continua"));

                log.LogError(ex, "Error adjuntando en SFleet el documento {Archivo} de la póliza {Poliza}; se continúa.",
                    documento.Nombre, numeroPoliza);
            }
        }
    }

    /// <summary>
    /// Deja constancia en la bitácora sin arriesgar el lote. Ver el comentario equivalente en
    /// ProcesarLoteSiniestroHandler: escribir el registro es otro acceso a dbLumoSys, y hacerlo
    /// desde dentro de un catch puede abortar el barrido justo cuando la base es lo que falla.
    /// </summary>
    private async Task RegistrarEnBitacora(string mensaje, NivelBitacora nivel, CancellationToken ct)
    {
        try
        {
            await bitacora.GuardarAsync(mensaje, nivel, _idAplicacion, ct);
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "seguros.bitacora-escritura", ("mensaje", mensaje));
            log.LogError(ex, "No se pudo registrar en la bitácora: {Mensaje}", mensaje);
        }
    }

    private static GuardarPolizaCommand ConstruirComando(
        PolizaResumenSICAS resumen,
        PolizaDetalleSICAS detalle,
        PolizaPrimasSICAS primas,
        List<PolizaCoberturasSICAS> coberturas,
        List<PolizaCobranzaSICAS> cobranza)
    {
        var danos       = ObtenerCobertura(coberturas, "Daño Material");
        var roboTotal   = ObtenerCobertura(coberturas, "Robo total");
        var roboParcial = ObtenerCobertura(coberturas, "Robo parcial");
        var adaptaciones = ObtenerCobertura(coberturas, "Adaptaciones y conversiones D.M.");
        var accidentesConductor = ObtenerCobertura(coberturas, "Accidentes automovilísticos al conductor");
        var gastosMedicos = ObtenerCobertura(coberturas, "Gastos médicos ocupantes");
        var asistenciaJuridica = ObtenerCobertura(coberturas, "Asistencia jurídica");
        var asistenciaVial = ObtenerCobertura(coberturas, "Asistencia vial");
        var rcCruzada   = ObtenerCobertura(coberturas, "Responsabilidad Cruzada");
        var rcLuc       = ObtenerCobertura(coberturas, "Responsabilidad civil");
        var rcPasajeros = ObtenerCobertura(coberturas, "RC Pasajeros");

        decimal? primerRecibo = cobranza.Count > 0 ? cobranza[0].PrimaTotal ?? 0 : 0;
        decimal? subsecuente  = cobranza.Count > 1 ? cobranza[1].PrimaTotal ?? 0 : primerRecibo;

        string? broker = resumen.VendAbreviacion;
        if (string.IsNullOrEmpty(broker))
            broker = "Sicurika";

        int? inciso = string.IsNullOrEmpty(primas.Inciso) || primas.Inciso == "0"
            ? 1
            : int.TryParse(primas.Inciso, out var incisoParsed) ? incisoParsed : 1;

        decimal? valorAdaptacion = (detalle.EqEspSAseg ?? 0) + (detalle.AdapSAseg ?? 0);
        decimal? valorFactura = coberturas.Count > 0 ? ObtenerValorFactura(coberturas[0].Comentario) : null;

        return new GuardarPolizaCommand
        {
            // Si SICAS mandara el documento vacío, se propaga como cadena vacía y GuardarPolizaHandler
            // lo rechaza con "Número de póliza requerido". Dejarlo pasar como null solo cambiaría
            // ese mensaje claro por una referencia nula más abajo.
            Poliza                 = resumen.Documento ?? string.Empty,
            Aseguradora            = resumen.CiaAbreviacion,
            Beneficiario           = primas.Beneficiario,
            BeneficiarioPreferente = string.IsNullOrEmpty(primas.Beneficiario) ? "PENDIENTE" : null,
            Broker                 = broker,
            FormaPago              = resumen.FPago,
            // Contratante se usa únicamente para resolver EMP_ID contra el catálogo EMPRESAS.
            Contratante            = resumen.NombreCompleto,
            Vehiculo = new VehiculoCommand
            {
                Serie             = detalle.Serie ?? string.Empty,
                Inciso            = inciso,
                TipoPoliza        = primas.SRamoNombre,
                FechaInicio       = primas.FDesde,
                FechaVencimiento  = primas.FHasta,
                PrimaNeta         = primas.PrimaNeta ?? 0,
                PrimaTotal        = primas.PrimaTotal ?? 0,
                Derechos          = primas.Derechos ?? 0,
                Recargos          = primas.Recargos ?? 0,
                IVA               = primas.IVA ?? 0,
                PrimerRecibo      = primerRecibo,
                Subsecuente       = subsecuente,
                TipoUso           = detalle.UsoVehiculo,
                AdministracionCartera = "SICÚRIKA AGENTE DE SEGUROS",
                Ejecutivo         = primas.EjecutNombre,
                Pasajeros         = detalle.Ocupantes?.ToString(),
                GestionPago       = primas.CCobro_TXT,
                Cobertura         = "FEDERAL",
                Adaptacion        = $"{detalle.EqEsp} {detalle.Adap}".Trim(),
                ValorAdaptacion   = valorAdaptacion,
                CoberturasDanos      = danos.Cobertura,
                DeducibleDanos       = danos.Deducible,
                CoberturasRoboTotal  = roboTotal.Cobertura,
                DeducibleRoboTotal   = roboTotal.Deducible,
                RoboParcial          = roboParcial.Cobertura,
                DeducibleRoboParcial = roboParcial.Deducible,
                AdaptacionesConversiones = adaptaciones.Cobertura,
                AccidentesConductor      = accidentesConductor.Suma,
                GastosMedicosOcupantes   = gastosMedicos.Suma,
                AsistenciaJuridica       = asistenciaJuridica.Cobertura,
                AsistenciaVial           = asistenciaVial.Cobertura,
                ResponsabilidadCivilCruzada   = rcCruzada.Cobertura,
                DeducibleResponsabilidadCivil = rcCruzada.Deducible,
                ResponsabilidadCivilLimiteUnicoCombinado = rcLuc.Suma,
                ResponsabilidadCivilPasajeros = rcPasajeros.Cobertura,
                ResponsabilidadCivilExtranjero = "NO APLICA",
                ValorFactura      = valorFactura,
                TipoActivo        = "SUSTITUCIÓN"
            }
        };
    }

    /// <summary>
    /// Replica ObtenerCobertura del ETL original (C:\LumoSysGit\Seguros\Generales\Conversiones.cs):
    /// busca la cobertura por nombre exacto (insensible a mayúsculas) y la traduce según su tipo:
    /// AMPARADA/NO APLICA (texto), numérica (Suma), o texto crudo con corrección de typo y override
    /// "Valor comercial" para Daño Material/Robo Total. El deducible se parsea igual para todas.
    /// </summary>
    private static (string Cobertura, decimal? Suma, decimal? Deducible) ObtenerCobertura(
        List<PolizaCoberturasSICAS> coberturas, string tipo)
    {
        var encontrada = coberturas.FirstOrDefault(c =>
            string.Equals(c.Nombre, tipo, StringComparison.OrdinalIgnoreCase));

        if (encontrada is null)
            return ("NO APLICA", null, null);

        decimal? deducible = !string.IsNullOrEmpty(encontrada.DeducibleL)
            ? ObtenerValorFactura(encontrada.DeducibleL)
            : null;

        switch (tipo)
        {
            case "Robo parcial":
            case "Adaptaciones y conversiones D.M.":
            case "Adaptaciones y conversiones R.T.":
            case "RC Pasajeros":
            case "RC USA y Canadá":
            {
                bool amparada =
                    (encontrada.SumaAseg?.Contains("valor", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (encontrada.SumaAseg?.Contains("amparada", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (decimal.TryParse(encontrada.SumaAseg, out var sumaTexto) && sumaTexto > 0);

                return (amparada ? "AMPARADA" : "NO APLICA", null, deducible);
            }

            case "Accidentes automovilísticos al conductor":
            case "Gastos médicos ocupantes":
            case "Responsabilidad civil":
            {
                decimal? suma = decimal.TryParse(encontrada.SumaAseg, out var sumaNumerica) ? sumaNumerica : null;
                return (string.Empty, suma, deducible);
            }

            default:
            {
                string cobertura = encontrada.SumaAseg ?? string.Empty;
                cobertura = cobertura == "Amaprada" ? "Amparada" : cobertura;

                string nombreUpper = encontrada.Nombre?.ToUpper() ?? string.Empty;
                if (nombreUpper is "DAÑO MATERIAL" or "DAÑOS MATERIALES" or "ROBO TOTAL")
                    cobertura = cobertura != "Amparada" ? "Valor comercial" : cobertura;

                return (cobertura, null, deducible);
            }
        }
    }

    /// <summary>Replica ObtenerValorFactura: extrae la primera secuencia contigua de dígitos de un texto.</summary>
    private static decimal? ObtenerValorFactura(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
            return null;

        var match = Regex.Match(texto, @"\d+");
        return match.Success && decimal.TryParse(match.Value, out var valor) ? valor : 0;
    }
}

/// <summary>
/// Un documento de SICAS ya descargado, listo para sus dos destinos: el repositorio de LumoSys
/// (vía FTP) y la póliza en SFleet. Se conserva en memoria para no pedirle a SICAS el mismo
/// archivo dos veces; vive solo mientras se procesa esa póliza.
/// </summary>
public sealed record DocumentoDescargado(string Nombre, byte[] Bytes);

public sealed class AplicacionOptions
{
    public int Seguros { get; init; } = 5;
    public int Siniestros { get; init; } = 6;
}
