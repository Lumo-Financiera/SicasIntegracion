namespace LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;

public sealed class GuardarSiniestroCommand
{
    public string? Poliza { get; init; }
    public string? NoSerie { get; init; }
    /// <summary>Ya normalizado (ObtenerTipoSiniestro) por quien construye el comando.</summary>
    public string? TipoSiniestro { get; init; }
    public string? FechaEvento { get; init; }
    public string? Descripcion { get; init; }
    public string? NoSiniestro { get; init; }
    public string? NoReporte { get; init; }
    public int? IDSiniestro { get; init; }
    public string? FechaResolucion { get; init; }
    public decimal? MontoIndemnizable { get; init; }
    public decimal? MontoDeducible { get; init; }
    public decimal? MontoPrimasPendientes { get; init; }
    public decimal? MontoOtrosDescuentos { get; init; }
    public int? Inciso { get; init; }
    public List<EstatusCommand> Actualizaciones { get; init; } = [];
}

public sealed class EstatusCommand
{
    public string? Estatus { get; init; }
    public string? Comentarios { get; init; }
    public string? FechaEvento { get; init; }
    public string? FechaEstatus { get; init; }
    public int IdUser { get; init; } = 3;
    public string? NumReporte { get; init; }
    public string? Ejecutivo { get; init; }
}
