using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>Barrido adicional de ventana angosta (cada N minutos) para Siniestros — mismo
/// razonamiento que <see cref="SegurosEtlIntervaloBackgroundService"/>: complementa, no
/// reemplaza, al barrido diario amplio, y desacopla intervalo (cada cuánto corre) de ventana
/// (cuánto mira hacia atrás) para dar traslape entre corridas ante caídas cortas del servicio.</summary>
public sealed class SiniestrosEtlIntervaloBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    IMonitoreoEtl monitoreoEtl,
    ILogger<SiniestrosEtlIntervaloBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? intervaloMinutos = ObtenerMinutos("EtlSchedule:IntervaloMinutosSiniestros");
        if (intervaloMinutos is null)
        {
            log.LogInformation("ETL Siniestros (intervalo): deshabilitado (EtlSchedule:IntervaloMinutosSiniestros no configurado).");
            return;
        }

        int ventanaMinutos = ObtenerMinutos("EtlSchedule:VentanaMinutosSiniestros") ?? (intervaloMinutos.Value + 10);

        log.LogInformation("ETL Siniestros (intervalo): iniciado, cada {Minutos} min, ventana de {Ventana} min.",
            intervaloMinutos, ventanaMinutos);

        var descriptor = DescribirCorrida(intervaloMinutos.Value);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(intervaloMinutos.Value), stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            using var corrida = monitoreoEtl.IniciarCorrida(descriptor);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSiniestroHandler>();

                var cmd = new ProcesarLoteSiniestroCommand
                {
                    Desde = DateTime.Now.AddMinutes(-ventanaMinutos),
                    Hasta = DateTime.Now
                };

                corrida.Etiquetar("ventana_minutos", ventanaMinutos.ToString());
                corrida.Etiquetar("rango_desde", cmd.Desde?.ToString("dd/MM/yyyy HH:mm"));
                corrida.Etiquetar("rango_hasta", cmd.Hasta?.ToString("dd/MM/yyyy HH:mm"));

                log.LogInformation("ETL Siniestros (intervalo): iniciando procesamiento de lote.");
                await handler.Handle(cmd, stoppingToken);
                log.LogInformation("ETL Siniestros (intervalo): lote completado.");

                corrida.MarcarExito();
            }
            catch (OperationCanceledException)
            {
                corrida.MarcarCancelada();
                throw;
            }
            catch (Exception ex)
            {
                corrida.MarcarFallo(ex);
                log.LogError(ex, "ETL Siniestros (intervalo): error en ejecución programada.");
            }
        }
    }

    private static DescriptorCorridaEtl DescribirCorrida(int intervaloMinutos) => new()
    {
        Monitor          = "etl-siniestros-intervalo",
        Nombre           = "ETL Siniestros (intervalo)",
        Modulo           = "Siniestros",
        Disparador       = "programado-intervalo",
        IntervaloMinutos = intervaloMinutos,
        MaxDuracionMinutos = Math.Max(intervaloMinutos * 2, 30),
        MargenMinutos      = 10
    };

    private int? ObtenerMinutos(string configKey)
    {
        string? valor = config[configKey];
        return int.TryParse(valor, out int minutos) && minutos > 0 ? minutos : null;
    }
}
