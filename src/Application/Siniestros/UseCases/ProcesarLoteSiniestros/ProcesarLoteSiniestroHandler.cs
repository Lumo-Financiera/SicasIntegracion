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
    IBitacoraRepository bitacora,
    IOptions<AplicacionOptions> opciones,
    ILogger<ProcesarLoteSiniestroHandler> log)
{
    private readonly int _idAplicacion = opciones.Value.Siniestros;

    public async Task Handle(ProcesarLoteSiniestroCommand cmd, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(cmd.FolioSiniestro))
        {
            await ProcesarPorReporte(cmd.FolioSiniestro, ct);
            return;
        }

        await ProcesarLote(cmd.Desde ?? DateTime.Now.AddDays(-1), cmd.Hasta ?? DateTime.Now, ct);
    }

    private async Task ProcesarLote(DateTime desde, DateTime hasta, CancellationToken ct)
    {
        log.LogInformation("Iniciando lote Siniestros {Desde:dd/MM/yyyy} → {Hasta:dd/MM/yyyy}", desde, hasta);

        int ultimaPagina = 0;
        for (int pagina = 1; pagina <= 30; pagina++)
        {
            var siniestros = await sicasClient.BuscarSiniestrosVigentes(desde, hasta, pagina, ct);
            if (siniestros.Count == 0) break;

            ultimaPagina = pagina;
            log.LogInformation("Página {Pagina}: {Count} siniestros", pagina, siniestros.Count);

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
            await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
        }

        // Fase 2: comentarios de bitácora del rango
        await ProcesarBitacoraDia(desde, hasta, ct);

        log.LogInformation("Lote Siniestros finalizado.");
    }

    private async Task ProcesarPorReporte(string noReporte, CancellationToken ct)
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

        await ProcesarSiniestroCompleto(resultados[0], ct);
    }

    private async Task ProcesarSiniestroCompleto(SiniestroResumenSICAS siniestro, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(siniestro.NumReporte)) return;

        try
        {
            if (siniestro.IDDocto is null)
            {
                log.LogWarning("Siniestro {NoReporte}: sin IDDocto, no se puede resolver la serie.", siniestro.NumReporte);
                return;
            }

            // HDS00009 no trae la serie del vehículo — sale de una consulta aparte (HWS_DDETAIL),
            // igual que en Seguros (mismo patrón que el ETL legacy: PolizaDetalle es un objeto separado).
            var detalle = await sicasClient.BuscarDetalle(siniestro.IDDocto.Value, ct);
            if (detalle?.Serie is null)
            {
                log.LogWarning("Siniestro {NoReporte}: no se pudo resolver la serie del vehículo (IDDocto={IDDocto}).",
                    siniestro.NumReporte, siniestro.IDDocto);
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

            var cmd = ConstruirComando(siniestro, detalle);
            var resultado = await guardarHandler.Handle(cmd, ct);

            if (!resultado.Exitoso)
            {
                if (resultado.Omitido)
                {
                    log.LogInformation("Siniestro {NoReporte} omitido: {Mensaje}", siniestro.NumReporte, resultado.Mensaje);
                    return;
                }

                log.LogError("Error guardando siniestro {NoReporte}: {Mensaje}",
                    siniestro.NumReporte, resultado.Mensaje);
                await bitacora.GuardarAsync(
                    $"Error guardando siniestro {siniestro.NumReporte}: {resultado.Mensaje}",
                    NivelBitacora.Error, _idAplicacion, ct);
                return;
            }

            await SubirDocumentos(resultado.SiniestroId!.Value, siniestro.NumReporte, archivos, ct);

            log.LogInformation("Siniestro {NoReporte} procesado correctamente.", siniestro.NumReporte);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error procesando siniestro {NoReporte}", siniestro.NumReporte);
            await bitacora.GuardarAsync(
                $"Error procesando siniestro {siniestro.NumReporte}: {ex.Message}",
                NivelBitacora.Error, _idAplicacion, ct);
        }
    }

    private async Task SubirDocumentos(
        int siniestroId, string noReporte, List<ArchivoSICAS> archivos, CancellationToken ct)
    {
        foreach (var archivo in archivos.Where(a => !string.IsNullOrEmpty(a.PathWWW)))
        {
            string nombreBase = Path.GetFileNameWithoutExtension(archivo.NombreArchivo);
            if (await siniestroRepo.ExisteDocumentoAsync(siniestroId, nombreBase, ct))
                continue;

            var bytes = await sicasRestClient.DownloadFile(archivo.PathWWW!, ct);
            if (bytes is null || bytes.Length == 0)
            {
                log.LogWarning("No se pudo descargar el documento {Archivo} del siniestro {NoReporte}.",
                    archivo.NombreArchivo, noReporte);
                continue;
            }

            string? nombreFinal = await documentService.SubirDocumentoSiniestro(archivo.NombreArchivo, bytes, ct);
            if (nombreFinal is null)
            {
                log.LogWarning("No se pudo subir el documento {Archivo} del siniestro {NoReporte} al FTP.",
                    archivo.NombreArchivo, noReporte);
                continue;
            }

            await siniestroRepo.RegistrarDocumentoAsync(siniestroId, nombreFinal, ct);
        }
    }

    private async Task ProcesarBitacoraDia(DateTime desde, DateTime hasta, CancellationToken ct)
    {
        try
        {
            int totalComentarios = 0;
            int ultimaPagina = 0;

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
                    if (string.IsNullOrWhiteSpace(item.NumReporte)) continue;

                    int? siniestroId = await siniestroRepo.BuscarIdPorReporte(item.NumReporte, ct);
                    if (siniestroId is null) continue;

                    if (string.IsNullOrWhiteSpace(item.Comentarios)) continue;

                    DateTime fechaRegistro = DateTime.TryParse(item.FechaRegistro, out var fr) ? fr : DateTime.Now;

                    bool existe = await siniestroRepo.ExisteEstatusAsync(
                        siniestroId.Value, item.Comentarios, fechaRegistro, ct);

                    if (!existe)
                    {
                        await siniestroRepo.UpsertEstatusAsync(new DatosEstatus
                        {
                            Estatus       = ObtenerTipoEstatus(item.Estatus),
                            Comentarios   = item.Comentarios,
                            FechaEvento   = DateTime.TryParse(item.FechaEvento, out var fe) ? fe : null,
                            FechaRegistro = fechaRegistro,
                            IdUser        = item.IdUser ?? 3,
                            NumReporte    = item.NumReporte,
                            Ejecutivo     = item.Ejecutivo
                        }, siniestroId.Value, ct);
                    }
                }
            }

            log.LogInformation("Fase 2 — Bitácora {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}: {Count} comentarios en total",
                desde, hasta, totalComentarios);

            if (ultimaPagina == 30)
            {
                string msg = $"Bitácora de siniestros {desde:dd/MM/yyyy}-{hasta:dd/MM/yyyy} alcanzó el límite de 30 páginas " +
                             "(30,000 comentarios) — es posible que existan más comentarios sin procesar en este rango.";
                log.LogWarning(msg);
                await bitacora.GuardarAsync(msg, NivelBitacora.Aviso, _idAplicacion, ct);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error procesando bitácora del rango {Desde:dd/MM/yyyy}-{Hasta:dd/MM/yyyy}.", desde, hasta);
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

    /// <summary>Replica ObtenerTipoEstatus del ETL legacy: corrige acentos faltantes en el texto
    /// de estatus que manda la bitácora de SICAS antes de resolver contra TIPOS_ESTATUS.</summary>
    private static string? ObtenerTipoEstatus(string? estatus)
    {
        if (string.IsNullOrEmpty(estatus))
            return estatus;

        string texto = estatus.ToUpper();
        return texto switch
        {
            "FECHA DE RESOLUCION" => "FECHA DE RESOLUCIÓN",
            "CANCELACION" => "CANCELACIÓN",
            _ => estatus
        };
    }
}
