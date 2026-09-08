using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
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
    IMonitoreoEtl monitoreoEtl,
    ILogger<SegurosEtlBackgroundService> log) : BackgroundService
{
    private const string ClaveHorario = "EtlSchedule:Seguros";
    private const string HorarioPorOmision = "00:05";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Servicio ETL Seguros iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan horaEjecucion = ObtenerHoraEjecucion();
            TimeSpan delay         = TiempoHastaProximaEjecucion(horaEjecucion);
            log.LogInformation("ETL Seguros: próxima ejecución en {Delay}", delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            // La corrida se abre aquí y no antes del Task.Delay a propósito: el check-in "en
            // progreso" debe cubrir solo el trabajo real, no la espera hasta la próxima ejecución
            // (si abarcara la espera, Sentry daría la corrida por colgada al vencer MaxRuntime).
            using var corrida = monitoreoEtl.IniciarCorrida(DescribirCorrida(horaEjecucion));

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSeguroHandler>();

                log.LogInformation("ETL Seguros: iniciando procesamiento de lote.");
                await handler.Handle(new ProcesarLoteSeguroCommand(), stoppingToken);
                log.LogInformation("ETL Seguros: lote completado.");

                corrida.MarcarExito();
            }
            catch (OperationCanceledException)
            {
                // Paro del servicio de Windows: no es una corrida fallida. Se re-lanza para
                // conservar el comportamiento original (terminar ExecuteAsync), solo se marca
                // antes para que el monitor programado no genere una alerta falsa.
                corrida.MarcarCancelada();
                throw;
            }
            catch (Exception ex)
            {
                corrida.MarcarFallo(ex);
                log.LogError(ex, "ETL Seguros: error en ejecución programada.");
            }
        }
    }

    /// <summary>Datos con los que Sentry identifica y vigila esta corrida programada. El horario
    /// se traduce a crontab desde la misma config que gobierna el <c>Task.Delay</c>, para que el
    /// horario esperado en Sentry nunca se desincronice del real.</summary>
    private static DescriptorCorridaEtl DescribirCorrida(TimeSpan horaEjecucion) => new()
    {
        Monitor    = "etl-seguros-diario",
        Nombre     = "ETL Seguros (diario)",
        Modulo     = "Seguros",
        Disparador = "programado-diario",
        Crontab    = $"{horaEjecucion.Minutes} {horaEjecucion.Hours} * * *",
        // El barrido amplio recorre hasta 30 páginas con una pausa de 300 ms por registro: pasar
        // de 3 h significa que SICAS está respondiendo anormalmente lento o el lote se colgó.
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
