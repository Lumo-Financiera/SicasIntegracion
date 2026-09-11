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
            var polizas = await sicasClient.BuscarPolizasVigentes(desde, hasta, pagina, ct);
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
            await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
        }

        // Queda como etiqueta de la corrida para poder alertar en Sentry sobre barridos que
        // terminan "bien" con cero registros: ese fue el síntoma de los dos bugs de filtros de
        // fecha, invisible durante semanas porque un lote vacío no falla.
        monitoreo.Etiquetar("registros_encontrados", totalRegistros.ToString());

        log.LogInformation("Lote Seguros finalizado. {Total} pólizas encontradas en el rango.", totalRegistros);
    }

    private async Task ProcesarPorSerie(string serie, CancellationToken ct)
    {
        log.LogInformation("Procesando póliza por serie: {Serie}", serie);

        var detalle = await sicasClient.BuscarDetallePorSerie(serie, ct);
        if (detalle?.IDDocto is null)
        {
            log.LogWarning("Serie {Serie} no encontrada en SICAS.", serie);
            await bitacora.GuardarAsync($"Serie {serie} no encontrada en SICAS.", NivelBitacora.Aviso, _idAplicacion, ct);
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
            await bitacora.GuardarAsync($"Póliza {documento} no encontrada en SICAS.", NivelBitacora.Aviso, _idAplicacion, ct);
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
                await bitacora.GuardarAsync($"Error guardando póliza {resumen.Documento}: {resultado.Mensaje}",
                    NivelBitacora.Error, _idAplicacion, ct);
                return;
            }

            monitoreo.Rastrear("seguros.guardado", $"Póliza {resumen.Documento} guardada",
                ("poliza_id", resultado.PolizaId?.ToString()),
                ("vehiculo_id", resultado.VehiculoId?.ToString()));

            // Subir documentos directamente desde SICAS (sin disco local)
            log.LogInformation("IDDocto={IDDocto}: SICAS devolvió {Count} archivo(s) digitales.", idDocto, archivos.Count);
            foreach (var archivo in archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)))
            {
                var bytes = await sicasRestClient.DownloadFile(archivo.PathWWW!, ct);
                if (bytes is null || bytes.Length == 0)
                {
                    log.LogWarning("No se pudo descargar el documento {Archivo} de la póliza {Poliza}.",
                        archivo.NombreArchivo, resumen.Documento);

                    // DownloadFile ya reportó la causa técnica; esto añade a qué póliza pertenece,
                    // que es el dato con el que se reprocesa a mano.
                    monitoreo.ReportarFalloSilencioso("seguros.subir-documentos",
                        "El documento no se pudo descargar de SICAS",
                        "poliza-guardada-sin-este-documento",
                        ("poliza", resumen.Documento),
                        ("serie", detalle.Serie),
                        ("archivo", archivo.NombreArchivo));
                    continue;
                }

                await subirHandler.Handle(new SubirDocumentoPolizaCommand
                {
                    Serie         = detalle.Serie ?? string.Empty,
                    NombreArchivo = archivo.NombreArchivo,
                    Bytes         = bytes
                }, ct);
            }

            // Sincronizar con SFleet
            if (resultado.VehiculoId.HasValue)
                await SincronizarSFleet(resumen, detalle, primas, coberturas, resultado.VehiculoId.Value, archivos, ct);

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
            await bitacora.GuardarAsync($"Error procesando póliza IDDocto={idDocto}: {ex.Message}",
                NivelBitacora.Error, _idAplicacion, ct);
        }
    }

    private async Task SincronizarSFleet(
        PolizaResumenSICAS resumen,
        PolizaDetalleSICAS detalle,
        PolizaPrimasSICAS primas,
        List<PolizaCoberturasSICAS> coberturas,
        int vehiculoId,
        List<LumoSys.Integraciones.Domain.Shared.Interfaces.ArchivoSICAS> archivos,
        CancellationToken ct)
    {
        try
        {
            int? vehiculoSFleet = await sfleetClient.BuscarVehiculo(detalle.Serie ?? string.Empty, ct);
            if (vehiculoSFleet is null)
            {
                log.LogWarning("Serie {Serie} no encontrada en SFleet.", detalle.Serie);
                return;
            }

            int? polizaSFleetId = await sfleetClient.BuscarPoliza(resumen.Documento ?? string.Empty, ct);
            bool esEdicion = polizaSFleetId.HasValue;

            var solicitud = new LumoSys.Integraciones.Domain.Seguros.Models.SolicitudSFleet
            {
                NumeroPoliza  = resumen.Documento ?? string.Empty,
                Beneficiario  = primas.Beneficiario,
                Broker        = resumen.VendAbreviacion,
                FormaPago     = resumen.FPago,
                PrimaNeta     = primas.PrimaNeta ?? 0,
                PrimaTotal    = primas.PrimaTotal ?? 0,
                FechaInicio   = DateTime.TryParse(primas.FDesde, out var fi) ? fi : default,
                FechaVencimiento = DateTime.TryParse(primas.FHasta, out var fv) ? fv : default,
                ClienteVehiculoId = vehiculoSFleet.Value
            };

            await sfleetClient.GuardarPoliza(solicitud, esEdicion, vehiculoSFleet.Value, ct);
        }
        catch (Exception ex)
        {
            // Silenciado deliberado: la póliza ya quedó guardada en dbLumoSys y el ETL no debe
            // deshacerla porque SFleet (sistema aparte) esté caído o rechace la petición. Se
            // reporta con la marca de que quedó desincronizada, que es la acción pendiente real.
            monitoreo.Capturar(ex, "seguros.sincronizar-sfleet",
                ("poliza", resumen.Documento),
                ("serie", detalle.Serie),
                ("vehiculo_id", vehiculoId.ToString()),
                ("consecuencia", "guardada-en-lumosys-sin-sincronizar-sfleet"));

            log.LogError(ex, "Error sincronizando SFleet para póliza {Poliza}", resumen.Documento);
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
            Poliza                 = resumen.Documento,
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

public sealed class AplicacionOptions
{
    public int Seguros { get; init; } = 5;
    public int Siniestros { get; init; } = 6;
}
