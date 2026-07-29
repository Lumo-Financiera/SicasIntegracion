using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>Barrido adicional de ventana angosta (cada N minutos), para bajar el tiempo de
/// espera entre que SICAS captura una póliza y queda sincronizada, sin esperar al barrido
/// diario. Solo corre si `EtlSchedule:IntervaloMinutosSeguros` está configurado (>0); si no,
/// no hace nada. NO reemplaza a <see cref="SegurosEtlBackgroundService"/> — corre en paralelo,
/// como un complemento, no como sustituto: el barrido diario amplio sigue siendo quien
/// garantiza que, en el peor caso (caída larga), se recupere dentro de 24h.
///
/// El intervalo (cada cuánto corre) y la ventana (cuánto mira hacia atrás) son configs
/// independientes a propósito: con `VentanaMinutosSeguros` > `IntervaloMinutosSeguros` cada
/// corrida se traslapa con las anteriores, lo cual da margen extra ante caídas cortas del
/// servicio sin depender de un margen fijo — confirmado necesario con la póliza
/// L0000019660-0 (capturada 27/07/2026 16:33), que una ventana de solo ~30 min nunca alcanzó
/// a ver por una caída del servicio en ese momento.</summary>
public sealed class SegurosEtlIntervaloBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SegurosEtlIntervaloBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int? intervaloMinutos = ObtenerMinutos("EtlSchedule:IntervaloMinutosSeguros");
        if (intervaloMinutos is null)
        {
            log.LogInformation("ETL Seguros (intervalo): deshabilitado (EtlSchedule:IntervaloMinutosSeguros no configurado).");
            return;
        }

        int ventanaMinutos = ObtenerMinutos("EtlSchedule:VentanaMinutosSeguros") ?? (intervaloMinutos.Value + 10);

        log.LogInformation("ETL Seguros (intervalo): iniciado, cada {Minutos} min, ventana de {Ventana} min.",
            intervaloMinutos, ventanaMinutos);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(intervaloMinutos.Value), stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSeguroHandler>();

                var cmd = new ProcesarLoteSeguroCommand
                {
                    Desde = DateTime.Now.AddMinutes(-ventanaMinutos),
                    Hasta = DateTime.Now
                };

                log.LogInformation("ETL Seguros (intervalo): iniciando procesamiento de lote.");
                await handler.Handle(cmd, stoppingToken);
                log.LogInformation("ETL Seguros (intervalo): lote completado.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "ETL Seguros (intervalo): error en ejecución programada.");
            }
        }
    }

    private int? ObtenerMinutos(string configKey)
    {
        string? valor = config[configKey];
        return int.TryParse(valor, out int minutos) && minutos > 0 ? minutos : null;
    }
}
