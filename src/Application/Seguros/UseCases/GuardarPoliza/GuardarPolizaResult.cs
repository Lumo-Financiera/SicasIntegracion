namespace LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;

public sealed class GuardarPolizaResult
{
    public bool Exitoso { get; init; }
    /// <summary>true cuando la serie no pertenece a la flotilla (no existe en COMPRAS_DETALLES/VEHICULOS)
    /// y por eso no se guardó nada — no es un error, es fuera de alcance para este integrador.</summary>
    public bool Omitido { get; init; }
    public string? Mensaje { get; init; }
    public int? PolizaId { get; init; }
    public int? VehiculoId { get; init; }

    public static GuardarPolizaResult Ok(int polizaId, int vehiculoId) =>
        new() { Exitoso = true, PolizaId = polizaId, VehiculoId = vehiculoId };

    public static GuardarPolizaResult Fallo(string mensaje) =>
        new() { Exitoso = false, Mensaje = mensaje };

    public static GuardarPolizaResult Omitir(string mensaje) =>
        new() { Exitoso = false, Omitido = true, Mensaje = mensaje };
}
