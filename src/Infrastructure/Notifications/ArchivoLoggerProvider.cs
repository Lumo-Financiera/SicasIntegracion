using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Proveedor de ILogger que manda todo el log del proyecto al archivo diario.
///
/// Existe porque la app corre como servicio de Windows: no hay consola, y UseWindowsService()
/// solo deja el Event Log a partir de Warning. Sin este proveedor, TODOS los LogInformation
/// del ETL (inicio de lote, páginas, registros procesados, omitidos) se perdían sin dejar rastro
/// — solo persistía lo que pasaba por IBitacoraRepository, unos pocos call-sites.
///
/// El alias "Archivo" permite afinar niveles por proveedor desde appsettings:
/// "Logging": { "Archivo": { "LogLevel": { "Default": "Information" } } }</summary>
[ProviderAlias("Archivo")]
public sealed class ArchivoLoggerProvider(
    LogErroresArchivoService archivo,
    IOptions<LogArchivoOptions> opts) : ILoggerProvider, ISupportExternalScope
{
    private readonly LogArchivoOptions _opts = opts.Value;
    private IExternalScopeProvider? _scopes;

    public ILogger CreateLogger(string categoryName) =>
        new ArchivoLogger(categoryName, archivo, _opts, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }
}

internal sealed class ArchivoLogger(
    string categoria,
    LogErroresArchivoService archivo,
    LogArchivoOptions opts,
    Func<IExternalScopeProvider?> scopes) : ILogger
{
    /// <summary>Solo el nombre de la clase, sin el namespace completo — el archivo se lee a ojo.</summary>
    private readonly string _categoriaCorta = categoria[(categoria.LastIndexOf('.') + 1)..];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        scopes()?.Push(state);

    // El filtrado real por nivel lo hace el LoggerFactory con las reglas de appsettings;
    // aquí solo se descarta lo que explícitamente no se quiere escribir nunca.
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var sb = new StringBuilder(160);
        sb.Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss.fff"))
          .Append(" [").Append(Abreviar(logLevel)).Append(']');

        if (opts.IncluirCategoria)
            sb.Append(' ').Append(_categoriaCorta);

        AgregarScopes(sb);

        sb.Append(' ').Append(formatter(state, ex));

        if (ex is not null)
        {
            sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

            // La traza completa solo para errores: en Warning ensuciaría el archivo.
            if (logLevel >= LogLevel.Error && ex.StackTrace is not null)
                sb.Append(Environment.NewLine).Append(ex.StackTrace);
        }

        archivo.Encolar(sb.ToString());
    }

    /// <summary>Aplana los scopes activos para poder seguir una corrida completa en el archivo
    /// (ej. {lote=a3f9c2 modo=intervalo}).
    ///
    /// Descarta los scopes que mete ASP.NET por su cuenta (TraceId, RequestPath, ActionName…):
    /// en un ETL no aportan y hacían cada línea tres veces más larga que el mensaje real.</summary>
    private void AgregarScopes(StringBuilder sb)
    {
        var proveedor = scopes();
        if (proveedor is null) return;

        var partes = new List<string>(4);

        proveedor.ForEachScope((scope, acumulado) =>
        {
            switch (scope)
            {
                case IEnumerable<KeyValuePair<string, object?>> pares:
                    foreach (var par in pares)
                    {
                        if (EsRuidoDeInfraestructura(par.Key)) continue;
                        acumulado.Add($"{par.Key}={par.Value}");
                    }
                    break;

                case not null:
                    string texto = scope.ToString() ?? string.Empty;
                    if (texto.Length > 0) acumulado.Add(texto);
                    break;
            }
        }, partes);

        if (partes.Count == 0) return;

        sb.Append(" {").AppendJoin(' ', partes).Append('}');
    }

    private static bool EsRuidoDeInfraestructura(string clave) => clave is
        "{OriginalFormat}" or                              // texto crudo del scope, redundante
        "SpanId" or "TraceId" or "ParentId" or              // tracing de Activity
        "ConnectionId" or "RequestId" or "RequestPath" or   // Kestrel / hosting
        "ActionId" or "ActionName";                         // MVC

    private static string Abreviar(LogLevel nivel) => nivel switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRI",
        _                    => "???"
    };
}
