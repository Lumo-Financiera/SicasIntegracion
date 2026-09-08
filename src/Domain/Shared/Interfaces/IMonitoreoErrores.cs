namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

/// <summary>
/// Puerto de monitoreo/observabilidad. La implementación real (Sentry) vive en Infrastructure
/// (<c>SentryMonitoreoErrores</c>) — Domain y Application solo conocen esta interfaz, por la
/// misma regla de capas que ya aplica a EF Core y HTTP: la lógica de negocio no se acopla a un
/// SDK de terceros.
///
/// Nota importante sobre qué NO hace falta instrumentar a mano: la integración de Sentry con
/// <c>ILogger</c> ya convierte automáticamente en evento cualquier <c>log.LogError(ex, ...)</c>
/// (ver <c>Sentry:MinimumEventLevel</c> en appsettings). Esta interfaz es para lo que esa
/// captura automática no puede dar por sí sola: etiquetas de negocio, datos extra y la
/// secuencia de pasos previos al fallo.
/// </summary>
public interface IMonitoreoErrores
{
    /// <summary>
    /// Abre un ámbito aislado (scope) para una unidad de trabajo: las etiquetas y datos que se
    /// agreguen dentro solo aplican a los eventos de ese ámbito y se descartan al liberarlo.
    /// Se propaga por el flujo <c>async</c> de las llamadas hijas. Debe consumirse con <c>using</c>
    /// — indispensable en los loops de los BackgroundServices, para que el contexto de un ítem
    /// no se filtre al siguiente.
    /// </summary>
    IDisposable IniciarAmbito(string operacion, params (string Clave, string? Valor)[] etiquetas);

    /// <summary>Etiqueta indexada y filtrable en Sentry. Solo para valores de baja cardinalidad
    /// (módulo, disparador, folio) — los valores nulos o vacíos se ignoran.</summary>
    void Etiquetar(string clave, string? valor);

    /// <summary>Dato adicional del evento (no indexado): útil para payloads, rangos y contadores.</summary>
    void AgregarDato(string clave, object? valor);

    /// <summary>Deja un rastro (breadcrumb) del paso ejecutado. Los rastros no generan eventos por
    /// sí solos: se adjuntan al siguiente error del ámbito para reconstruir qué pasó antes.</summary>
    void Rastrear(string categoria, string mensaje, params (string Clave, string? Valor)[] datos);

    /// <summary>Igual que <see cref="Rastrear"/>, pero marca el paso como fallido.</summary>
    void RastrearFallo(string categoria, string mensaje, params (string Clave, string? Valor)[] datos);

    /// <summary>Envía la excepción a Sentry con etiquetas propias del evento. Usar solo donde la
    /// excepción se silencia deliberadamente por regla de negocio (si se re-lanza, basta con el
    /// <c>log.LogError</c> y el enriquecimiento del ámbito).</summary>
    void Capturar(Exception ex, string operacion, params (string Clave, string? Valor)[] etiquetas);
}
