using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>Barrido diario amplio (ayer→hoy) — corre SIEMPRE, sin importar si el modo de
/// intervalo (<see cref="SiniestrosEtlIntervaloBackgroundService"/>) está activo. Es la red de
/// seguridad: si el servicio estuvo caído o el barrido de intervalo se saltó algo por su
/// ventana angosta, este barrido diario eventualmente lo recupera (máximo 24h de rezago).</summary>
public sealed class SiniestrosEtlBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    IMonitoreoEtl monitoreoEtl,
    ILogger<SiniestrosEtlBackgroundService> log) : BackgroundService
{
    private const string ClaveHorario = "EtlSchedule:Siniestros";
    private const string HorarioPorOmision = "00:10";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Servicio ETL Siniestros iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan horaEjecucion = ObtenerHoraEjecucion();
            TimeSpan delay         = TiempoHastaProximaEjecucion(horaEjecucion);
            log.LogInformation("ETL Siniestros: próxima ejecución en {Delay}", delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            // Ver comentario equivalente en SegurosEtlBackgroundService: el check-in "en progreso"
            // cubre solo el trabajo, no la espera hasta la próxima ejecución.
            using var corrida = monitoreoEtl.IniciarCorrida(DescribirCorrida(horaEjecucion));

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSiniestroHandler>();

                log.LogInformation("ETL Siniestros: iniciando procesamiento de lote.");
                await handler.Handle(new ProcesarLoteSiniestroCommand(), stoppingToken);
                log.LogInformation("ETL Siniestros: lote completado.");

                corrida.MarcarExito();
            }
            catch (OperationCanceledException)
            {
                // Paro del servicio: se marca cancelada y se re-lanza, igual que antes de la
                // instrumentación (el filtro `when` original tampoco la atrapaba).
                corrida.MarcarCancelada();
                throw;
            }
            catch (Exception ex)
            {
                corrida.MarcarFallo(ex);
                log.LogError(ex, "ETL Siniestros: error en ejecución programada.");
            }
        }
    }

    /// <summary>Datos con los que Sentry identifica y vigila esta corrida programada. Incluye la
    /// Fase 2 (bitácora del rango), por eso el margen de duración es igual de holgado que en
    /// Seguros.</summary>
    private static DescriptorCorridaEtl DescribirCorrida(TimeSpan horaEjecucion) => new()
    {
        Monitor    = "etl-siniestros-diario",
        Nombre     = "ETL Siniestros (diario)",
        Modulo     = "Siniestros",
        Disparador = "programado-diario",
        Crontab    = $"{horaEjecucion.Minutes} {horaEjecucion.Hours} * * *",
        MaxDuracionMinutos = 180,
        MargenMinutos      = 15
    };

    private TimeSpan ObtenerHoraEjecucion()
    {
        string horaStr = config[ClaveHorario] ?? HorarioPorOmision;
        return TimeSpan.TryParse(horaStr, out TimeSpan horaEjecucion)
            ? horaEjecucion
            : TimeSpan.Parse(HorarioPorOmision);
    }

    private static TimeSpan TiempoHastaProximaEjecucion(TimeSpan horaEjecucion)
    {
        DateTime ahora     = DateTime.Now;
        DateTime siguiente = DateTime.Today.Add(horaEjecucion);
        if (siguiente <= ahora)
            siguiente = siguiente.AddDays(1);

        return siguiente - ahora;
    }
}
