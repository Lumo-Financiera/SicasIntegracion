using System.Net;
using System.Text.Json;
using LumoSys.Integraciones.Infrastructure.Notifications;

namespace LumoSys.Integraciones.API.Middlewares;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    LogErroresArchivoService logArchivo,
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
