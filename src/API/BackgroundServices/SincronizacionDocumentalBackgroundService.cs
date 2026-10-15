using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>
/// Puesta al día de los documentos al arrancar el servicio.
///
/// Corre UNA vez por arranque, poco después de que el host levanta, y recorre TODOS los siniestros
/// vinculados a SICAS preguntándole al Centro Digital si tiene archivos que LumoSys no tenga. Es
/// decir: cada despliegue sincroniza por sí solo todo lo que haya quedado atrás, sin que nadie
/// tenga que entrar al servidor ni disparar nada a mano.
///
/// Por qué existe aparte del barrido diario: el barrido corre a las 00:10 y hace la revisión
/// documental en modo incremental. Tras un despliegue a media mañana habría que esperar a la
/// madrugada, y aun así el modo incremental no mira más atrás de su ventana. Esta pasada sí.
///
/// No duplica nada: <c>SubirDocumentos</c> consulta <c>ExisteDocumentoAsync</c> archivo por
/// archivo antes de descargar, así que un siniestro ya completo cuesta una llamada a SICAS y cero
/// escrituras. Repetirla en cada reinicio es seguro — solo gasta tiempo.
///
/// Si el FTP rechaza el acceso, el cortacircuitos del handler corta la pasada tras 5 fallos
/// seguidos y deja el evento crítico en Sentry con el código y la respuesta literal del servidor.
/// </summary>
public sealed class SincronizacionDocumentalBackgroundService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    IMonitoreoEtl monitoreoEtl,
    ILogger<SincronizacionDocumentalBackgroundService> log) : BackgroundService
{
    /// <summary>
    /// Margen para que el host termine de levantar antes de empezar a golpear SICAS y la base.
    /// Corto a propósito: la gracia es que la sincronización ocurra al desplegar, no media hora
    /// después.
    /// </summary>
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(2);

    private const string ClaveHorario = "EtlSchedule:Documentos";

    /// <summary>
    /// Hora de la pasada nocturna. 01:00 por omisión: después del barrido de siniestros (00:10),
    /// para no competir con él por SICAS.
    /// </summary>
    private const string HorarioPorOmision = "01:00";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1) Al entrar en línea. Es lo que hace que un despliegue se ponga al día solo, sin
        //    esperar a la madrugada ni que nadie entre al servidor.
        log.LogInformation("Sincronización documental: pasada de arranque en {Espera}.", EsperaInicial);

        try
        {
            await Task.Delay(EsperaInicial, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (stoppingToken.IsCancellationRequested) return;

        await EjecutarPasadaCompleta("arranque", stoppingToken);

        // 2) Y después, todas las noches. Cubre el punto ciego del modo incremental: un siniestro
        //    antiguo, cerrado y que YA tiene documentos no entra en ninguno de sus conjuntos, así
        //    que si SICAS le agrega un archivo después, solo la pasada completa lo ve. Medido el
        //    13/10/2026: 91 siniestros de 2022-2023 con 140 archivos en esa situación.
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan hora  = ObtenerHoraEjecucion();
            TimeSpan espera = TiempoHastaProximaEjecucion(hora);
            log.LogInformation("Sincronización documental: próxima pasada nocturna en {Espera}.", espera);

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (stoppingToken.IsCancellationRequested) return;

            await EjecutarPasadaCompleta("programado-diario", stoppingToken);
        }
    }

    private TimeSpan ObtenerHoraEjecucion()
    {
        string horaStr = config[ClaveHorario] ?? HorarioPorOmision;
        return TimeSpan.TryParse(horaStr, out TimeSpan hora) ? hora : TimeSpan.Parse(HorarioPorOmision);
    }

    private static TimeSpan TiempoHastaProximaEjecucion(TimeSpan hora)
    {
        DateTime ahora     = DateTime.Now;
        DateTime siguiente = DateTime.Today.Add(hora);
        if (siguiente <= ahora)
            siguiente = siguiente.AddDays(1);

        return siguiente - ahora;
    }

    private async Task EjecutarPasadaCompleta(string disparador, CancellationToken stoppingToken)
    {
        using var corrida = monitoreoEtl.IniciarCorrida(DescribirCorrida(disparador));

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<ProcesarLoteSiniestroHandler>();

            log.LogInformation("Sincronización documental: iniciando pasada COMPLETA sobre todos los siniestros vinculados a SICAS.");

            await handler.Handle(new ProcesarLoteSiniestroCommand
            {
                SoloDocumentos   = true,
                RevisionCompleta = true
            }, stoppingToken);

            log.LogInformation("Sincronización documental: pasada completa terminada.");
            corrida.MarcarExito();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Paro del servicio: no es una corrida fallida. Mismo criterio que los demás
            // BackgroundServices — ver el comentario extenso en SiniestrosEtlBackgroundService
            // sobre por qué el filtro del token importa.
            corrida.MarcarCancelada();
        }
        catch (Exception ex)
        {
            // No se re-lanza: que la puesta al día falle no debe tumbar el servicio de Windows ni
            // impedir que corran los barridos programados ni las pasadas siguientes. Queda como
            // corrida fallida en Sentry.
            corrida.MarcarFallo(ex);
            log.LogError(ex, "Sincronización documental: error en la pasada completa.");
        }
    }

    /// <summary>
    /// La pasada de arranque NO declara crontab ni intervalo: ocurre cuando se despliega, no a una
    /// hora esperable, y si Sentry la vigilara como corrida programada marcaría ausencia cada día
    /// que no haya despliegue. La nocturna sí se vigila.
    /// </summary>
    private DescriptorCorridaEtl DescribirCorrida(string disparador)
    {
        bool esProgramada = disparador == "programado-diario";
        TimeSpan hora = ObtenerHoraEjecucion();

        return new DescriptorCorridaEtl
        {
            Monitor    = esProgramada ? "sincronizacion-documental-diaria" : "sincronizacion-documental-arranque",
            Nombre     = esProgramada ? "Sincronización documental (diaria)" : "Sincronización documental (al arrancar)",
            Modulo     = "Siniestros",
            Disparador = disparador,
            Crontab    = esProgramada ? $"{hora.Minutes} {hora.Hours} * * *" : null,
            // Margen amplio: recorre el universo completo, una llamada a SICAS por siniestro
            // (1,408 al 13/10/2026, unos 12-15 minutos).
            MaxDuracionMinutos = 240,
            MargenMinutos      = 60
        };
    }
}
