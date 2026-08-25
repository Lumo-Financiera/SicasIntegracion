using System.Text.RegularExpressions;
using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Domain.Shared.Models;
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
    IOptions<AplicacionOptions> opciones,
    ILogger<ProcesarLoteSeguroHandler> log)
{
    private readonly int _idAplicacion = opciones.Value.Seguros;

    public async Task Handle(ProcesarLoteSeguroCommand cmd, CancellationToken ct = default)
    {
        string modo = !string.IsNullOrWhiteSpace(cmd.Poliza) ? "poliza"
                    : !string.IsNullOrWhiteSpace(cmd.Serie)  ? "serie"
                    : cmd.Desde.HasValue || cmd.Hasta.HasValue ? "rango"
                    : "diario";

        var resumen = new ResumenLote("Seguros", modo);

        // Scope de correlación: marca todas las líneas de esta corrida con el mismo id, para
        // poder aislarlas cuando el barrido diario y el de intervalo se traslapan en el archivo.
        using var _ = log.BeginScope(new Dictionary<string, object?>
        {
            ["lote"] = resumen.CorrelacionId,
            ["modo"] = modo
        });

        try
        {
            if (!string.IsNullOrWhiteSpace(cmd.Poliza))
            {
                await ProcesarPorDocumento(cmd.Poliza, resumen, ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(cmd.Serie))
            {
                await ProcesarPorSerie(cmd.Serie, resumen, ct);
                return;
            }

            await ProcesarLote(cmd.Desde ?? DateTime.Now.AddDays(-1), cmd.Hasta ?? DateTime.Now, resumen, ct);
        }
        finally
        {
            // Se emite siempre, incluso si el lote se cortó por cancelación: saber hasta dónde
            // llegó es justamente lo que hace falta en ese caso.
            if (resumen.TieneIncidencias)
                log.LogWarning("{Resumen}", resumen.Resumir());
            else
                log.LogInformation("{Resumen}", resumen.Resumir());
        }
    }

    private async Task ProcesarLote(DateTime desde, DateTime hasta, ResumenLote resumen, CancellationToken ct)
    {
        log.LogInformation("Iniciando lote Seguros {Desde:dd/MM/yyyy} -> {Hasta:dd/MM/yyyy}", desde, hasta);

        int ultimaPagina = 0;
        for (int pagina = 1; pagina <= 30; pagina++)
        {
            var polizas = await sicasClient.BuscarPolizasVigentes(desde, hasta, pagina, ct);
            if (polizas.Count == 0) break;

            ultimaPagina = pagina;
            resumen.Inc(ResumenLote.Paginas);
            resumen.Inc(ResumenLote.Leidos, polizas.Count);
            log.LogInformation("Pagina {Pagina}: {Count} polizas", pagina, polizas.Count);

            foreach (var poliza in polizas)
            {
                if (poliza.IDDocto is null)
                {
                    log.LogWarning("Poliza {Documento} sin IDDocto; se omite.", poliza.Documento);
                    resumen.Inc(ResumenLote.OmitidosSinDatos);
                    continue;
                }

                await ProcesarPolizaCompleta(poliza, resumen, ct);
                await Task.Delay(300, ct); // evita ráfagas hacia SICAS (throttling observado en pruebas)
            }
        }

        if (ultimaPagina == 30)
        {
            string msg = $"Barrido de pólizas {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                         "(3,000 registros) — es posible que existan más pólizas sin procesar en este rango.";
            log.LogWarning("{Mensaje}", msg);
            await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
        }

        log.LogInformation("Lote Seguros finalizado.");
    }

    private async Task ProcesarPorSerie(string serie, ResumenLote resumen, CancellationToken ct)
    {
        log.LogInformation("Procesando poliza por serie: {Serie}", serie);

        var detalle = await sicasClient.BuscarDetallePorSerie(serie, ct);
        if (detalle?.IDDocto is null)
        {
            log.LogWarning("Serie {Serie} no encontrada en SICAS.", serie);
            await bitacora.GuardarAsync($"Serie {serie} no encontrada en SICAS.", NivelBitacora.Aviso, _idAplicacion, ct);
            return;
        }

        var resumenPoliza = await sicasClient.BuscarPolizaPorIdDocto(detalle.IDDocto.Value, ct);
        if (resumenPoliza is null)
        {
            log.LogWarning("Poliza IDDocto={IDDocto} (serie {Serie}) no encontrada como vigente en H03117.",
                detalle.IDDocto, serie);
            resumen.Inc(ResumenLote.OmitidosSinDatos);
            return;
        }

        resumen.Inc(ResumenLote.Leidos);
        await ProcesarPolizaCompleta(resumenPoliza, resumen, ct);
    }

    private async Task ProcesarPorDocumento(string documento, ResumenLote resumen, CancellationToken ct)
    {
        log.LogInformation("Procesando poliza por folio: {Documento}", documento);

        int? idDocto = await sicasClient.BuscarIdDoctoporDocumento(documento, ct);
        if (idDocto is null)
        {
            log.LogWarning("Poliza {Documento} no encontrada en SICAS.", documento);
            await bitacora.GuardarAsync($"Póliza {documento} no encontrada en SICAS.", NivelBitacora.Aviso, _idAplicacion, ct);
            return;
        }

        var resumenPoliza = await sicasClient.BuscarPolizaPorIdDocto(idDocto.Value, ct);
        if (resumenPoliza is null)
        {
            log.LogWarning("Poliza {Documento} (IDDocto={IDDocto}) no activa en H03117.", documento, idDocto);
            resumen.Inc(ResumenLote.OmitidosSinDatos);
            return;
        }

        resumen.Inc(ResumenLote.Leidos);
        await ProcesarPolizaCompleta(resumenPoliza, resumen, ct);
    }

    private async Task ProcesarPolizaCompleta(
        PolizaResumenSICAS resumenPoliza, ResumenLote resumen, CancellationToken ct)
    {
        int idDocto = resumenPoliza.IDDocto!.Value;

        try
        {
            var detalle = await sicasClient.BuscarDetalle(idDocto, ct);
            if (detalle is null)
            {
                log.LogWarning("Poliza {Poliza} (IDDocto={IDDocto}): HWS_DDETAIL no devolvio detalle; se omite.",
                    resumenPoliza.Documento, idDocto);
                resumen.Inc(ResumenLote.OmitidosSinDatos);
                return;
            }

            // Filtra antes de gastar más llamadas a SICAS: si la serie no es un vehículo propio,
            // no hay nada que guardar (evita registros huérfanos/duplicados, ver ExisteVehiculoAsync).
            if (!await polizaRepo.ExisteVehiculoAsync(detalle.Serie ?? string.Empty, ct))
            {
                log.LogInformation("Poliza {Poliza} (serie {Serie}) no pertenece a la flotilla propia; se omite.",
                    resumenPoliza.Documento, detalle.Serie);
                resumen.Inc(ResumenLote.OmitidosFlotilla);
                return;
            }

            var primas     = await sicasClient.BuscarPrimas(idDocto, ct);
            var coberturas = await sicasClient.BuscarCoberturas(idDocto, ct);
            var cobranza   = await sicasClient.BuscarCobranza(idDocto, ct);
            var archivos   = await sicasClient.BuscarDigital(idDocto, ct);

            if (primas is null)
            {
                log.LogWarning("Poliza {Poliza} (IDDocto={IDDocto}): H03400 no devolvio primas; se omite.",
                    resumenPoliza.Documento, idDocto);
                resumen.Inc(ResumenLote.OmitidosSinDatos);
                return;
            }

            log.LogDebug("Poliza {Poliza}: {Cob} coberturas, {Cob2} recibos de cobranza, {Arch} archivos.",
                resumenPoliza.Documento, coberturas.Count, cobranza.Count, archivos.Count);

            var cmd = ConstruirComando(resumenPoliza, detalle, primas, coberturas, cobranza);
            var resultado = await guardarHandler.Handle(cmd, ct);

            if (!resultado.Exitoso)
            {
                if (resultado.Omitido)
                {
                    log.LogInformation("Poliza {Poliza} omitida: {Mensaje}", resumenPoliza.Documento, resultado.Mensaje);
                    resumen.Inc(ResumenLote.OmitidosFlotilla);
                    return;
                }

                log.LogError("Error guardando poliza {Poliza}: {Mensaje}", resumenPoliza.Documento, resultado.Mensaje);
                resumen.Inc(ResumenLote.Errores);
                await bitacora.GuardarAsync($"Error guardando póliza {resumenPoliza.Documento}: {resultado.Mensaje}",
                    NivelBitacora.Error, _idAplicacion, ct);
                return;
            }

            resumen.Inc(ResumenLote.Procesados);

            // Subir documentos directamente desde SICAS (sin disco local)
            if (archivos.Count > 0)
                log.LogInformation("Poliza {Poliza} (IDDocto={IDDocto}): SICAS devolvio {Count} archivo(s) digitales.",
                    resumenPoliza.Documento, idDocto, archivos.Count);

            foreach (var archivo in archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)))
            {
                var bytes = await sicasRestClient.DownloadFile(archivo.PathWWW!, ct);
                if (bytes is null || bytes.Length == 0)
                {
                    log.LogWarning("No se pudo descargar el documento {Archivo} de la poliza {Poliza}.",
                        archivo.NombreArchivo, resumenPoliza.Documento);
                    resumen.Inc(ResumenLote.DocsFallidos);
                    continue;
                }

                int arcId = await subirHandler.Handle(new SubirDocumentoPolizaCommand
                {
                    Serie         = detalle.Serie ?? string.Empty,
                    NombreArchivo = archivo.NombreArchivo,
                    Bytes         = bytes
                }, ct);

                if (arcId > 0) resumen.Inc(ResumenLote.DocsSubidos);
                else           resumen.Inc(ResumenLote.DocsFallidos);
            }

            // Sincronizar con SFleet
            if (resultado.VehiculoId.HasValue)
                await SincronizarSFleet(resumenPoliza, detalle, primas, coberturas, resultado.VehiculoId.Value,
                    archivos, resumen, ct);

            log.LogInformation("Poliza {Poliza} ({Serie}) procesada correctamente (PolizaId={PolizaId}).",
                resumenPoliza.Documento, detalle.Serie, resultado.PolizaId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error procesando IDDocto={IDDocto} (poliza {Poliza})", idDocto, resumenPoliza.Documento);
            resumen.Inc(ResumenLote.Errores);
            await bitacora.GuardarAsync($"Error procesando póliza IDDocto={idDocto}: {ex.Message}",
                NivelBitacora.Error, _idAplicacion, ct);
        }
    }

    private async Task SincronizarSFleet(
        PolizaResumenSICAS resumenPoliza,
        PolizaDetalleSICAS detalle,
        PolizaPrimasSICAS primas,
        List<PolizaCoberturasSICAS> coberturas,
        int vehiculoId,
        List<ArchivoSICAS> archivos,
        ResumenLote resumen,
        CancellationToken ct)
    {
        try
        {
            int? vehiculoSFleet = await sfleetClient.BuscarVehiculo(detalle.Serie ?? string.Empty, ct);
            if (vehiculoSFleet is null)
            {
                log.LogWarning("Serie {Serie} no encontrada en SFleet; la poliza {Poliza} queda sin replicar alla.",
                    detalle.Serie, resumenPoliza.Documento);
                resumen.Inc(ResumenLote.SFleetFallo);
                return;
            }

            int? polizaSFleetId = await sfleetClient.BuscarPoliza(resumenPoliza.Documento ?? string.Empty, ct);
            bool esEdicion = polizaSFleetId.HasValue;

            var solicitud = new SolicitudSFleet
            {
                NumeroPoliza  = resumenPoliza.Documento ?? string.Empty,
                Beneficiario  = primas.Beneficiario,
                Broker        = resumenPoliza.VendAbreviacion,
                FormaPago     = resumenPoliza.FPago,
                PrimaNeta     = primas.PrimaNeta ?? 0,
                PrimaTotal    = primas.PrimaTotal ?? 0,
                FechaInicio   = DateTime.TryParse(primas.FDesde, out var fi) ? fi : default,
                FechaVencimiento = DateTime.TryParse(primas.FHasta, out var fv) ? fv : default,
                ClienteVehiculoId = vehiculoSFleet.Value
            };

            int idSFleet = await sfleetClient.GuardarPoliza(solicitud, esEdicion, vehiculoSFleet.Value, ct);

            if (idSFleet > 0)
            {
                resumen.Inc(ResumenLote.SFleetOk);
                log.LogInformation("SFleet: poliza {Poliza} {Accion} (id={Id}, vehiculo={Vehiculo}).",
                    resumenPoliza.Documento, esEdicion ? "actualizada" : "creada", idSFleet, vehiculoSFleet);
            }
            else
            {
                resumen.Inc(ResumenLote.SFleetFallo);
                log.LogWarning("SFleet: no se pudo guardar la poliza {Poliza} (vehiculo={Vehiculo}).",
                    resumenPoliza.Documento, vehiculoSFleet);
            }
        }
        catch (Exception ex)
        {
            // SFleet es secundario: su falla no invalida lo ya guardado en dbLumoSys.
            log.LogError(ex, "Error sincronizando SFleet para poliza {Poliza}", resumenPoliza.Documento);
            resumen.Inc(ResumenLote.SFleetFallo);
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
