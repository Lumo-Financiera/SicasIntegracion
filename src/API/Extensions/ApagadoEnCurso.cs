namespace LumoSys.Integraciones.API.Extensions;

/// <summary>
/// Señal de "el host se está deteniendo", consultable desde donde no hay inyección de dependencias.
///
/// Existe por una sola razón: <c>SetBeforeSend</c> de Sentry es una función estática sin acceso al
/// contenedor, y necesita distinguir una cancelación provocada por el apagado del servicio (ruido
/// esperable en cada reinicio) de una provocada por un <c>Timeout</c> de <c>HttpClient</c> contra
/// SICAS/SFleet/FTP (una falla real que debe alertar). Ambas llegan como
/// <c>TaskCanceledException</c> con el token ya cancelado, así que la excepción por sí sola no
/// permite separarlas — el estado del host sí.
///
/// La registra <c>Program.cs</c> contra <c>IHostApplicationLifetime.ApplicationStopping</c>.
/// </summary>
public static class ApagadoEnCurso
{
    private static volatile bool _activo;

    /// <summary>true desde que el host empieza a detenerse.</summary>
    public static bool Activo => _activo;

    public static void Marcar() => _activo = true;
}
