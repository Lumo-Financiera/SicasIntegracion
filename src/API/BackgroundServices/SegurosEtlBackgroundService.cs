using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>Barrido diario amplio (ayer→hoy) — corre SIEMPRE, sin importar si el modo de
/// intervalo (<see cref="SegurosEtlIntervaloBackgroundService"/>) está activo. Es la red de
/// seguridad: si el servicio estuvo caído o el barrido de intervalo se saltó algo por su
/// ventana angosta, este barrido diario eventualmente lo recupera (máximo 24h de rezago).</summary>
public sealed class SegurosEtlBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SegurosEtlBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Servicio ETL Seguros iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = TiempoHastaProximaEjecucion("EtlSchedule:Seguros", "00:05");
            log.LogInformation("ETL Seguros: próxima ejecución en {Delay}", delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSeguroHandler>();

                log.LogInformation("ETL Seguros: iniciando procesamiento de lote.");
                await handler.Handle(new ProcesarLoteSeguroCommand(), stoppingToken);
                log.LogInformation("ETL Seguros: lote completado.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "ETL Seguros: error en ejecución programada.");
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
