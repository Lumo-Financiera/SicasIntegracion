using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Infrastructure.Documents;
using LumoSys.Integraciones.Infrastructure.SFleet;
using LumoSys.Integraciones.Infrastructure.SICAS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.API.BackgroundServices;

/// <summary>
/// Al arrancar, mide desde ESTE servidor si cada destino externo se puede alcanzar y lo reporta a
/// Sentry.
///
/// Razón de ser: quien opera el integrador no entra al servidor y no puede dar por hecho que la
/// configuración publicada sea la misma que tiene en su copia local. Sin esto, ante un fallo había
/// que adivinar a qué host y puerto estaba intentando conectarse realmente el proceso. Ahora, a los
/// pocos segundos de cada despliegue, queda en Sentry un evento con los endpoints EFECTIVOS, las
/// IPs a las que resuelven y si el puerto acepta conexión — listo para copiarse en un ticket de
/// soporte.
///
/// Nunca incluye contraseñas. El usuario de FTP sí se incluye porque es justo el dato que soporte
/// necesita para revisar permisos, y no es un secreto por sí solo.
/// </summary>
public sealed class DiagnosticoConectividadBackgroundService(
    IDiagnosticoRed diagnostico,
    IMonitoreoErrores monitoreo,
    IOptions<SICASOptions> sicas,
    IOptions<SFleetOptions> sfleet,
    IOptions<FtpOptions> ftp,
    ILogger<DiagnosticoConectividadBackgroundService> log) : BackgroundService
{
    /// <summary>
    /// El host donde SICAS publica los ARCHIVOS, que no es el de la API. Se sondea aparte porque
    /// es exactamente el que falló en producción el 14/10/2026 ("Host desconocido"), y porque el
    /// nombre no aparece en ninguna configuración: viene dentro del PathWWW que devuelve GetFiles.
    /// </summary>
    private const string HostArchivosSicas = "www.sicasonline.info";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Margen breve para no competir con el arranque del host.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (stoppingToken.IsCancellationRequested) return;

        try
        {
            var sondeos = new List<SondeoDestino>
            {
                await diagnostico.SondearUrl("SICAS · API (token, reportes, GetFiles)", sicas.Value.BaseUrl, stoppingToken),
                await diagnostico.SondearHost("SICAS · host de los ARCHIVOS (PathWWW)", HostArchivosSicas, 443, stoppingToken),
                await diagnostico.SondearUrl("Fleet / SFleet · API", sfleet.Value.BaseUrl, stoppingToken),
                await diagnostico.SondearHost("FTP de documentos (control)", ftp.Value.Host, 21, stoppingToken)
            };

            foreach (var s in sondeos)
                log.LogInformation("Conectividad — {Resumen}", s.Resumen);

            var caidos = sondeos.Where(s => !s.Alcanzable).ToList();

            var datos = new List<(string, string?)>
            {
                ("sicas_api",      sicas.Value.BaseUrl),
                ("sicas_archivos", $"https://{HostArchivosSicas}"),
                ("sfleet_api",     sfleet.Value.BaseUrl),
                ("ftp_host",       ftp.Value.Host),
                ("ftp_usuario",    ftp.Value.Usuario),
                ("ftp_ruta_polizas",    ftp.Value.RutaDestinoPolizas),
                ("ftp_ruta_siniestros", ftp.Value.RutaDestinoSiniestros)
            };

            foreach (var s in sondeos)
                datos.Add(($"destino_{Normalizar(s.Destino)}", s.Resumen));

            if (caidos.Count == 0)
            {
                // Todo alcanzable: se deja como rastro, sin generar evento. El silencio es la señal
                // de que no hay nada que pedirle a soporte.
                monitoreo.Rastrear("diagnostico.conectividad",
                    "Todos los destinos externos son alcanzables desde el servidor", datos.ToArray());
                log.LogInformation("Conectividad: los {N} destinos externos responden.", sondeos.Count);
                return;
            }

            string queHacer = string.Join("  ||  ", caidos.Select(c => c.QuePedir));

            monitoreo.ReportarFalloCritico("diagnostico.conectividad",
                $"{caidos.Count} de {sondeos.Count} destinos externos NO son alcanzables desde el servidor: " +
                string.Join(" ; ", caidos.Select(c => $"{c.Host}:{c.Puerto}")),
                "las-integraciones-que-dependen-de-esos-destinos-no-van-a-funcionar",
                [.. datos, ("que_pedir_a_soporte", queHacer)]);

            log.LogError("Conectividad: {N} destino(s) inalcanzable(s). {Detalle}",
                caidos.Count, string.Join(" | ", caidos.Select(c => c.Resumen)));
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Que el diagnóstico falle no debe impedir que el servicio opere.
            monitoreo.Capturar(ex, "diagnostico.conectividad",
                ("consecuencia", "no-se-pudo-medir-la-conectividad-al-arrancar"));
            log.LogError(ex, "Error ejecutando el diagnóstico de conectividad.");
        }
    }

    private static string Normalizar(string texto) =>
        new(texto.ToLowerInvariant()
                 .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                 .ToArray());
}
