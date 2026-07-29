namespace LumoSys.Integraciones.Domain.Seguros.Models;

public sealed class SolicitudSFleet
{
    public string NumeroPoliza { get; init; } = string.Empty;
    public string? Beneficiario { get; init; }
    public string? Broker { get; init; }
    public string? FormaPago { get; init; }
    public decimal PrimaNeta { get; init; }
    public decimal PrimaTotal { get; init; }
    public DateTime FechaInicio { get; init; }
    public DateTime FechaVencimiento { get; init; }
    public string? IdAseguradora { get; init; }
    public int ClienteVehiculoId { get; init; }
    public string? Cobertura { get; init; }
}
