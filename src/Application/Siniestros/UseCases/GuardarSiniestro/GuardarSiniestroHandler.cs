using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Models;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;

public sealed class GuardarSiniestroHandler(
    ISiniestroRepository repo,
    ILogger<GuardarSiniestroHandler> log)
{
    public async Task<GuardarSiniestroResult> Handle(GuardarSiniestroCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.NoReporte))
            return GuardarSiniestroResult.Fallo("Número de reporte requerido.");

        try
        {
            if (!await repo.ExisteVehiculoAsync(cmd.NoSerie ?? string.Empty, ct))
            {
                string msg = $"Serie {cmd.NoSerie} no está registrada como vehículo propio " +
                             "(no existe en COMPRAS_DETALLES); se omite, no pertenece a esta flotilla.";
                log.LogInformation(msg);
                return GuardarSiniestroResult.Omitir(msg);
            }

            var datosSiniestro = new DatosSiniestro
            {
                NumeroPoliza      = cmd.Poliza ?? string.Empty,
                NoSerie           = cmd.NoSerie,
                TipoSiniestro     = cmd.TipoSiniestro,
                FechaEvento       = ParseFecha(cmd.FechaEvento),
                Descripcion       = cmd.Descripcion,
                NoSiniestro       = cmd.NoSiniestro,
                NoReporte         = cmd.NoReporte,
                IDSiniestro       = cmd.IDSiniestro,
                FechaResolucion   = ParseFecha(cmd.FechaResolucion),
                MontoIndemnizable = cmd.MontoIndemnizable,
                MontoDeducible    = cmd.MontoDeducible,
                MontoPrimasPendientes = cmd.MontoPrimasPendientes,
                MontoOtrosDescuentos  = cmd.MontoOtrosDescuentos,
                Inciso            = cmd.Inciso
            };

            int siniestroId = await repo.UpsertSiniestroAsync(datosSiniestro, ct);

            foreach (var est in cmd.Actualizaciones)
            {
                if (est.Estatus != "SOLICITUD" && string.IsNullOrWhiteSpace(est.Comentarios))
                    continue;

                var datosEstatus = new DatosEstatus
                {
                    Estatus       = est.Estatus,
                    Comentarios   = est.Comentarios,
                    FechaEvento   = ParseFecha(est.FechaEvento),
                    FechaRegistro = ParseFecha(est.FechaEstatus),
                    IdUser        = est.IdUser,
                    NumReporte    = est.NumReporte,
                    Ejecutivo     = est.Ejecutivo
                };

                await repo.UpsertEstatusAsync(datosEstatus, siniestroId, ct);
            }

            log.LogInformation("Siniestro {NoReporte} guardado. SiniestroId={SiniestroId}",
                cmd.NoReporte, siniestroId);

            return GuardarSiniestroResult.Ok(siniestroId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error guardando siniestro {NoReporte}", cmd.NoReporte);
            throw; // el middleware global convierte esto en 500
        }
    }

    private static DateTime? ParseFecha(string? valor) =>
        DateTime.TryParse(valor, out var d) ? d : null;
}
