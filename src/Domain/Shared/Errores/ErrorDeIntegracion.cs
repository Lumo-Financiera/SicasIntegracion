namespace LumoSys.Integraciones.Domain.Shared.Errores;

/// <summary>
/// Un servicio externo —SICAS, SFleet o el FTP— no respondió, o respondió que no puede atender la
/// petición.
///
/// Se separa de <see cref="ErrorDeNegocio"/> porque la reacción es distinta: aquí no hay ningún
/// dato que corregir, hay un sistema que no está disponible. Y se separa por código de respuesta
/// porque tampoco todos pesan igual: un 404 o un 422 afectan a un registro concreto, mientras que
/// quedarse sin respuesta o recibir un 5xx suele significar que no va a pasar ninguno.
/// </summary>
/// <param name="servicio">Quién no respondió: "sicas", "sfleet", "ftp".</param>
/// <param name="codigoHttp">Código de la respuesta, o null si no llegó a haber respuesta.</param>
public class ErrorDeIntegracion(string servicio, int? codigoHttp, string mensaje, Exception? interna = null)
    : Exception(mensaje, interna)
{
    public string Servicio { get; } = servicio;
    public int? CodigoHttp { get; } = codigoHttp;

    /// <summary>
    /// true cuando el fallo afecta solo a este registro: el servicio contestó, y contestó que con
    /// esta petición concreta no puede. Un 4xx distinto de 408 y 429 entra aquí.
    ///
    /// Lo contrario —sin respuesta, 5xx, timeout o límite de tasa agotado— apunta a que el servicio
    /// está caído y ningún registro va a pasar, que es lo que sí merece despertar a alguien.
    /// </summary>
    public bool AfectaSoloAEsteRegistro =>
        CodigoHttp is >= 400 and < 500 and not 408 and not 429;
}
