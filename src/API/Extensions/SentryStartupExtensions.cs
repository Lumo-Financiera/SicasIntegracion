using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace LumoSys.Integraciones.API.Extensions;

/// <summary>
/// Inicialización centralizada de Sentry, equivalente al bloque <c>#region Sentry</c> de
/// <c>Application_Start</c> en lumo-system (<c>slnLumoSys/Global.asax.cs</c>), trasladado al
/// modelo de hosting de ASP.NET Core:
///
/// <list type="bullet">
/// <item><c>SentrySdk.Init(...)</c> + <c>Application_End → _sentry.Dispose()</c> ⟶
///       <c>UseSentry(...)</c>, que ata el ciclo de vida del SDK al del host (incluye el
///       vaciado de la cola pendiente al detener el servicio de Windows).</item>
/// <item><c>Application_Error → SentrySdk.CaptureException</c> ⟶ middleware de Sentry +
///       captura explícita en <c>ExceptionHandlingMiddleware</c>.</item>
/// <item><c>Application_BeginRequest/EndRequest → Start/FinishSentryTransaction</c> ⟶
///       <c>AutoRegisterTracing</c> (activado por omisión) para las peticiones HTTP, y
///       <c>SentryMonitoreoEtl</c> para las corridas de fondo, que no tienen request.</item>
/// <item><c>AddEntityFramework()</c> (EF6) ⟶ integración de <c>DiagnosticSource</c>, incluida en
///       Sentry.AspNetCore, que instrumenta EF Core sin configuración adicional.</item>
/// <item><c>ConfigurationManager.AppSettings["SENTRY_*"]</c> ⟶ sección <c>Sentry</c> de
///       <c>appsettings.json</c>, que Sentry.AspNetCore bindea por convención.</item>
/// </list>
/// </summary>
public static class SentryStartupExtensions
{
    /// <summary>Nombre del servicio, como etiqueta fija en todo evento — este proceso es el único
    /// que reporta a este proyecto de Sentry hoy, pero deja la puerta abierta a distinguirlo si
    /// se agregan más.</summary>
    private const string NombreServicio = "LumoSysIntegraciones";

    /// <summary>Rutas que no vale la pena rastrear en Performance (documentación de la API y
    /// pruebas de disponibilidad). Equivale a la clave <c>SENTRY_EXCLUDES</c> de lumo-system, que
    /// ahí se lee de config; aquí también se puede extender por config con <c>Sentry:Excludes</c>.</summary>
    private static readonly string[] ExclusionesBase = ["/swagger", "/health", "/favicon.ico"];

    public static WebApplicationBuilder ConfigurarSentry(this WebApplicationBuilder builder)
    {
        IConfiguration cfg = builder.Configuration;
        bool esDesarrollo  = builder.Environment.IsDevelopment();

        string[] exclusiones =
        [
            .. ExclusionesBase,
            .. (cfg["Sentry:Excludes"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];

        builder.WebHost.UseSentry((SentryAspNetCoreOptions opciones) =>
        {
            // El DSN identifica el proyecto en Sentry; no es un secreto (viaja en cada evento),
            // por eso vive en el appsettings versionado. Un DSN vacío deja al SDK deshabilitado
            // y la aplicación corre igual — útil para entornos donde no se quiere reportar.
            opciones.Dsn = LeerTexto(cfg["Sentry:Dsn"]) ?? string.Empty;

            // Igual que lumo-system: más muestreo en desarrollo (0.8, en appsettings.Development.json)
            // que en producción (0.2, en appsettings.json). Los literales de abajo son solo la red
            // de seguridad para cuando la clave no está configurada.
            opciones.TracesSampleRate = LeerDouble(cfg["Sentry:TracesSampleRate"], esDesarrollo ? 0.8 : 0.2);

            opciones.Environment     = LeerTexto(cfg["Sentry:Environment"]) ?? (esDesarrollo ? "develop" : "production");
            opciones.Release         = ResolverRelease(cfg, esDesarrollo);
            opciones.Debug           = bool.TryParse(cfg["Sentry:Debug"], out bool debug) && debug;
            opciones.SendDefaultPii  = true;
            opciones.StackTraceMode  = StackTraceMode.Enhanced;
            opciones.AttachStacktrace = true;
            opciones.ServerName      = System.Environment.MachineName;
            opciones.DefaultTags["servicio"] = NombreServicio;

            // Corre como servicio de Windows: al detenerlo hay que darle margen para vaciar la
            // cola, o los eventos del último ciclo del ETL se pierden en el apagado.
            opciones.ShutdownTimeout = TimeSpan.FromSeconds(5);

            // Integración con ILogger: todo `log.LogError(ex, ...)` que ya existe en el código se
            // vuelve un evento de Sentry, con los parámetros estructurados del mensaje como datos
            // extra. Es lo que hace innecesario envolver en try-catch los métodos que ya loguean.
            opciones.MinimumEventLevel      = LogLevel.Error;
            opciones.MinimumBreadcrumbLevel = LogLevel.Information;

            // Explícito y no por omisión: es lo que evita el evento duplicado cuando un punto de
            // entrada reporta la excepción a mano (para adjuntar contexto) y además la loguea con
            // `log.LogError`, o cuando la misma excepción se loguea en dos niveles del ETL.
            // Se deja fuera InnerException: dos fallas distintas que comparten causa raíz son
            // eventos distintos y hay que verlos por separado.
            opciones.DeduplicateMode = DeduplicateMode.SameEvent
                                     | DeduplicateMode.SameExceptionInstance
                                     | DeduplicateMode.AggregateException;

            opciones.TracesSampler = contexto => MuestrearTransaccion(contexto, opciones.TracesSampleRate, exclusiones);

            opciones.SetBeforeSend(DescartarRuido);
        });

        return builder;
    }

    /// <summary>
    /// Las corridas del ETL se rastrean al 100% (son pocas al día y su duración es justamente el
    /// dato que interesa vigilar), mientras el tráfico HTTP sigue el muestreo configurado y las
    /// rutas excluidas no se rastrean. Mismo mecanismo que el <c>TracesSampler</c> de lumo-system.
    /// </summary>
    private static double? MuestrearTransaccion(
        TransactionSamplingContext contexto, double? tasaBase, string[] exclusiones)
    {
        if (contexto.TransactionContext.Operation == "etl.run")
            return 1.0;

        string nombre = contexto.TransactionContext.Name;
        if (exclusiones.Any(exclusion => nombre.Contains(exclusion, StringComparison.OrdinalIgnoreCase)))
            return 0.0;

        return tasaBase;
    }

    /// <summary>
    /// Descarta lo que no representa una falla real:
    /// <list type="bullet">
    /// <item>Cancelaciones — al detener el servicio de Windows, los <c>Task.Delay</c> de los cuatro
    ///       BackgroundServices y las peticiones en vuelo lanzan <c>OperationCanceledException</c>;
    ///       reportarlas convertiría cada reinicio en una ráfaga de alertas falsas.</item>
    /// </list>
    /// </summary>
    private static SentryEvent? DescartarRuido(SentryEvent evento, SentryHint _)
    {
        if (EsCancelacion(evento.Exception))
            return null;

        return evento;
    }

    private static bool EsCancelacion(Exception? ex) => ex switch
    {
        null                        => false,
        OperationCanceledException  => true, // cubre también TaskCanceledException
        AggregateException agregada => agregada.InnerExceptions.All(EsCancelacion),
        _                           => false
    };

    /// <summary>
    /// Replica el criterio de release de lumo-system (<c>"v" + Version</c> en producción, sufijo de
    /// ambiente fuera de ella), tomando la versión del ensamblado cuando no se configura una
    /// explícita. El release es lo que permite a Sentry decir "este error apareció con el
    /// despliegue de ayer".
    /// </summary>
    private static string ResolverRelease(IConfiguration cfg, bool esDesarrollo)
    {
        if (LeerTexto(cfg["Sentry:Release"]) is { } configurada)
            return configurada;

        string version = typeof(SentryStartupExtensions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return esDesarrollo ? $"v{version}-develop" : $"v{version}";
    }

    /// <summary>Una clave presente pero vacía en appsettings cuenta como no configurada — así el
    /// archivo versionado puede documentar las claves disponibles sin imponer un valor.</summary>
    private static string? LeerTexto(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static double LeerDouble(string? valor, double porOmision) =>
        double.TryParse(valor, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double resultado)
            ? resultado
            : porOmision;
}
