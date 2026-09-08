using System.Net;
using System.Text.Json;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Infrastructure.Notifications;

namespace LumoSys.Integraciones.API.Middlewares;

/// <summary>
/// Red de seguridad de las peticiones HTTP: convierte cualquier excepción no controlada en un 500
/// con cuerpo JSON, la deja en el log diario de archivo y la reporta a Sentry. Es el equivalente
/// de <c>Application_Error → SentrySdk.CaptureException(...)</c> de lumo-system
/// (<c>slnLumoSys/Global.asax.cs</c>).
///
/// Este middleware consume la excepción (no la re-lanza) para poder responder el JSON de error —
/// contrato de la API que no se toca. Por eso la captura es explícita: al no propagarse, el
/// middleware de Sentry que va antes en el pipeline nunca la vería.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    LogErroresArchivoService logArchivo,
    IMonitoreoErrores monitoreo,
    ILogger<ExceptionHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // Se reporta antes de loguear: la deduplicación de Sentry (DeduplicateMode, ver
            // SentryStartupExtensions) descarta el evento que la integración de ILogger generaría
            // para la misma excepción, así que el que sobrevive es este, con contexto HTTP.
            monitoreo.AgregarDato("traza_peticion", context.TraceIdentifier);
            monitoreo.AgregarDato("query_string", context.Request.QueryString.ToString());
            monitoreo.Capturar(ex, "http.excepcion-no-controlada",
                ("http_metodo", context.Request.Method),
                ("http_ruta", context.Request.Path.Value));

            log.LogError(ex, "Error no controlado en {Method} {Path}",
                context.Request.Method, context.Request.Path);

            // CancellationToken.None a propósito: si la excepción es justamente por cancelación
            // de la petición (RequestAborted), ese token ya está cancelado y no debe impedir
            // que el error quede escrito en el log.
            await logArchivo.EscribirAsync(
                $"[EXCEPCION NO CONTROLADA] {context.Request.Method} {context.Request.Path} | {ex.GetType().Name}: {ex.Message}",
                CancellationToken.None);

            context.Response.StatusCode  = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/json";

            var respuesta = new
            {
                Estatus  = false,
                Mensaje  = "Error interno del servidor.",
                Detalle  = ex.Message
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(respuesta));
        }
    }
}
