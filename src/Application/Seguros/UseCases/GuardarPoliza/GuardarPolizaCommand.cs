namespace LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;

public sealed class GuardarPolizaCommand
{
    public string? Aseguradora { get; init; }
    public string Poliza { get; init; } = string.Empty;
    public string? Beneficiario { get; init; }
    public string? BeneficiarioPreferente { get; init; }
    public string? Broker { get; init; }
    public string? FormaPago { get; init; }
    /// <summary>Usado únicamente para resolver EMP_ID contra el catálogo EMPRESAS; no se persiste como texto.</summary>
    public string? Contratante { get; init; }
    public VehiculoCommand? Vehiculo { get; init; }
}

public sealed class VehiculoCommand
{
    public string Serie { get; init; } = string.Empty;
    public int? Inciso { get; init; }
    public string? TipoPoliza { get; init; }
    public string? FechaInicio { get; init; }
    public string? FechaVencimiento { get; init; }
    public decimal? PrimaNeta { get; init; }
    public decimal? PrimaTotal { get; init; }
    public decimal? Derechos { get; init; }
    public decimal? Recargos { get; init; }
    public decimal? IVA { get; init; }
    public decimal? PrimerRecibo { get; init; }
    public decimal? Subsecuente { get; init; }
    public string? TipoUso { get; init; }
    public string? AdministracionCartera { get; init; }
    public string? Ejecutivo { get; init; }
    public string? Pasajeros { get; init; }
    public string? GestionPago { get; init; }
    public string? Cobertura { get; init; }
    public string? Adaptacion { get; init; }
    public decimal? ValorAdaptacion { get; init; }
    public string? CoberturasDanos { get; init; }
    public decimal? DeducibleDanos { get; init; }
    public string? CoberturasRoboTotal { get; init; }
    public decimal? DeducibleRoboTotal { get; init; }
    public string? RoboParcial { get; init; }
    public decimal? DeducibleRoboParcial { get; init; }
    public string? AdaptacionesConversiones { get; init; }
    public decimal? AccidentesConductor { get; init; }
    public decimal? GastosMedicosOcupantes { get; init; }
    public string? AsistenciaJuridica { get; init; }
    public string? AsistenciaVial { get; init; }
    public string? ResponsabilidadCivilCruzada { get; init; }
    public decimal? DeducibleResponsabilidadCivil { get; init; }
    public decimal? ResponsabilidadCivilLimiteUnicoCombinado { get; init; }
    public string? ResponsabilidadCivilPasajeros { get; init; }
    public string? ResponsabilidadCivilExtranjero { get; init; }
    public decimal? ValorFactura { get; init; }
    public string TipoActivo { get; init; } = "SUSTITUCIÓN";
}
