using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
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
    ILogger<SiniestrosEtlBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Servicio ETL Siniestros iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = TiempoHastaProximaEjecucion("EtlSchedule:Siniestros", "00:10");
            log.LogInformation("ETL Siniestros: próxima ejecución en {Delay}", delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSiniestroHandler>();

                log.LogInformation("ETL Siniestros: iniciando procesamiento de lote.");
                await handler.Handle(new ProcesarLoteSiniestroCommand(), stoppingToken);
                log.LogInformation("ETL Siniestros: lote completado.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "ETL Siniestros: error en ejecución programada.");
            }
        }
    }

    private TimeSpan TiempoHastaProximaEjecucion(string configKey, string defaultTime)
    {
        string horaStr = config[configKey] ?? defaultTime;
        if (!TimeSpan.TryParse(horaStr, out TimeSpan horaEjecucion))
            horaEjecucion = TimeSpan.Parse(defaultTime);

        DateTime ahora     = DateTime.Now;
        DateTime siguiente = DateTime.Today.Add(horaEjecucion);
        if (siguiente <= ahora)
            siguiente = siguiente.AddDays(1);

        return siguiente - ahora;
    }
}
