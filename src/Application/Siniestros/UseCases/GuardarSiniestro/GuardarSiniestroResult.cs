namespace LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;

public sealed class GuardarSiniestroResult
{
    public bool Exitoso { get; init; }
    /// <summary>true cuando la serie no pertenece a la flotilla (no existe en COMPRAS_DETALLES)
    /// y por eso no se guardó nada — no es un error, es fuera de alcance para este integrador.</summary>
    public bool Omitido { get; init; }
    public string? Mensaje { get; init; }
    public int? SiniestroId { get; init; }

    public static GuardarSiniestroResult Ok(int siniestroId) =>
        new() { Exitoso = true, SiniestroId = siniestroId };

    public static GuardarSiniestroResult Fallo(string mensaje) =>
        new() { Exitoso = false, Mensaje = mensaje };

    public static GuardarSiniestroResult Omitir(string mensaje) =>
        new() { Exitoso = false, Omitido = true, Mensaje = mensaje };
}
