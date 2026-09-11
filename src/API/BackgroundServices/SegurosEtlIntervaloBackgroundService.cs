using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
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
    IMonitoreoEtl monitoreoEtl,
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

        var descriptor = DescribirCorrida(intervaloMinutos.Value);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(intervaloMinutos.Value), stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            using var corrida = monitoreoEtl.IniciarCorrida(descriptor);

            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSeguroHandler>();

                var cmd = new ProcesarLoteSeguroCommand
                {
                    Desde = DateTime.Now.AddMinutes(-ventanaMinutos),
                    Hasta = DateTime.Now
                };

                // La ventana consultada queda como etiqueta: si un registro se pierde, lo primero
                // que hay que poder responder en Sentry es qué rango miró esa corrida.
                corrida.Etiquetar("ventana_minutos", ventanaMinutos.ToString());
                corrida.Etiquetar("rango_desde", cmd.Desde?.ToString("dd/MM/yyyy HH:mm"));
                corrida.Etiquetar("rango_hasta", cmd.Hasta?.ToString("dd/MM/yyyy HH:mm"));

                log.LogInformation("ETL Seguros (intervalo): iniciando procesamiento de lote.");
                await handler.Handle(cmd, stoppingToken);
                log.LogInformation("ETL Seguros (intervalo): lote completado.");

                corrida.MarcarExito();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Paro del servicio de Windows: no es una corrida fallida. Se re-lanza para
                // conservar el comportamiento original (terminar ExecuteAsync), solo se marca
                // antes para que el monitor programado no genere una alerta falsa.
                //
                // El filtro `when (stoppingToken.IsCancellationRequested)` es esencial: sin él,
                // un Timeout de HttpClient contra SICAS llega aquí como TaskCanceledException
                // (hereda de OperationCanceledException), se reportaría como corrida CORRECTA al
                // monitor y el `throw;` mataría este servicio — y con él, todo el host.
                corrida.MarcarCancelada();
                throw;
            }
            catch (Exception ex)
            {
                // Incluye los timeouts de red: se reportan como corrida fallida y NO se re-lanzan,
                // para que una caída temporal de SICAS no tumbe el servicio de Windows entero.
                corrida.MarcarFallo(ex);
                log.LogError(ex, "ETL Seguros (intervalo): error en ejecución programada.");
            }
        }
    }

    /// <summary>Aquí el horario esperado se expresa como intervalo (no crontab), porque es como
    /// está configurado el servicio. La duración máxima se ata al propio intervalo: una corrida
    /// angosta que tarda más que su periodo indica que la ventana quedó chica o SICAS se degradó.</summary>
    private static DescriptorCorridaEtl DescribirCorrida(int intervaloMinutos) => new()
    {
        Monitor          = "etl-seguros-intervalo",
        Nombre           = "ETL Seguros (intervalo)",
        Modulo           = "Seguros",
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
