using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Sentry;

namespace LumoSys.Integraciones.Infrastructure.Monitoring;

/// <summary>
/// Implementación de <see cref="IMonitoreoErrores"/> sobre el SDK de Sentry.
///
/// Todas las operaciones son seguras cuando el SDK no está inicializado (sin DSN configurado):
/// el propio <c>SentrySdk</c> se comporta como no-op, así que la aplicación corre igual con o sin
/// monitoreo y no hay que condicionar los call-sites.
///
/// Es singleton porque el SDK de Sentry es estático por diseño; el aislamiento entre unidades de
/// trabajo lo da el ámbito (<see cref="IniciarAmbito"/>), que Sentry mantiene por flujo de
/// ejecución con <c>AsyncLocal</c> — no por instancia de esta clase.
/// </summary>
public sealed class SentryMonitoreoErrores : IMonitoreoErrores
{
    public IDisposable IniciarAmbito(string operacion, params (string Clave, string? Valor)[] etiquetas)
    {
        IDisposable ambito = SentrySdk.PushScope();
        SentrySdk.ConfigureScope(scope =>
        {
            scope.SetTag("operacion", operacion);
            AplicarEtiquetas(scope, etiquetas);
        });
        return ambito;
    }

    public void Etiquetar(string clave, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return;
        SentrySdk.ConfigureScope(scope => scope.SetTag(clave, valor));
    }

    public void AgregarDato(string clave, object? valor)
    {
        if (valor is null) return;
        SentrySdk.ConfigureScope(scope => scope.SetExtra(clave, valor));
    }

    public void Rastrear(string categoria, string mensaje, params (string Clave, string? Valor)[] datos) =>
        SentrySdk.AddBreadcrumb(mensaje, categoria, data: ConstruirDatos(datos), level: BreadcrumbLevel.Info);

    public void RastrearFallo(string categoria, string mensaje, params (string Clave, string? Valor)[] datos) =>
        SentrySdk.AddBreadcrumb(mensaje, categoria, data: ConstruirDatos(datos), level: BreadcrumbLevel.Error);

    public void Capturar(Exception ex, string operacion, params (string Clave, string? Valor)[] etiquetas) =>
        SentrySdk.CaptureException(ex, scope =>
        {
            scope.SetTag("operacion", operacion);
            AplicarEtiquetas(scope, etiquetas);
        });

    public void ReportarFalloSilencioso(string operacion, string motivo, string consecuencia,
        params (string Clave, string? Valor)[] etiquetas) =>
        // Warning y no Error: estos fallos afectan a un registro concreto —un documento que no
        // subió, una serie que SFleet no tiene— y deben quedar registrados para auditoría, pero
        // no justifican un correo. Cuando el FTP lleva semanas caído, este mismo punto se dispara
        // cientos de veces por corrida; con nivel Error eso era una tormenta de notificaciones
        // sobre un problema ya conocido. Lo que sí alerta va por ReportarFalloCritico.
        Reportar(operacion, motivo, consecuencia, SentryLevel.Warning, "informativa", etiquetas);

    public void ReportarFalloCritico(string operacion, string motivo, string consecuencia,
        params (string Clave, string? Valor)[] etiquetas) =>
        Reportar(operacion, motivo, consecuencia, SentryLevel.Fatal, "critica", etiquetas);

    /// <summary>
    /// CaptureMessage y no CaptureException: no hay excepción que capturar, ese es justamente el
    /// problema. El mensaje lleva el punto de fallo al inicio para que Sentry agrupe todos los
    /// eventos del mismo punto en un solo issue.
    /// </summary>
    private static void Reportar(string operacion, string motivo, string consecuencia,
        SentryLevel nivel, string alerta, (string Clave, string? Valor)[] etiquetas) =>
        SentrySdk.CaptureMessage($"[{operacion}] {motivo}", scope =>
        {
            scope.SetTag("operacion", operacion);
            scope.SetTag("consecuencia", consecuencia);
            // Sobre esta etiqueta se escriben las reglas de notificación; el clasificador de
            // SentryStartupExtensions respeta la que venga puesta desde aquí.
            scope.SetTag("alerta", alerta);
            // Permite filtrar en Sentry exactamente esta clase de falla: la que no produce
            // excepción, no rompe nada visible y solo se nota cuando alguien echa de menos un dato.
            scope.SetTag("fallo_silencioso", "si");
            AplicarEtiquetas(scope, etiquetas);
        }, nivel);

    private static void AplicarEtiquetas(Scope scope, (string Clave, string? Valor)[] etiquetas)
    {
        foreach (var (clave, valor) in etiquetas)
        {
            // Sentry indexa las etiquetas: una etiqueta vacía es ruido en los filtros, se omite.
            if (!string.IsNullOrWhiteSpace(valor))
                scope.SetTag(clave, valor);
        }
    }

    private static IDictionary<string, string>? ConstruirDatos((string Clave, string? Valor)[] datos)
    {
        if (datos.Length == 0) return null;

        var mapa = new Dictionary<string, string>(datos.Length);
        foreach (var (clave, valor) in datos)
        {
            if (!string.IsNullOrWhiteSpace(valor))
                mapa[clave] = valor;
        }
        return mapa.Count > 0 ? mapa : null;
    }
}
