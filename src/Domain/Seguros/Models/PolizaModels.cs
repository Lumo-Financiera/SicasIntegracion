namespace LumoSys.Integraciones.Domain.Seguros.Models;

public sealed class PolizaResumenSICAS
{
    public int? IDDocto { get; init; }
    public string? Documento { get; init; }
    public string? Inciso { get; init; }
    public string? FDesde { get; init; }
    public string? FHasta { get; init; }
    public string? CiaAbreviacion { get; init; }
    public string? NombreCompleto { get; init; }
    public string? VendAbreviacion { get; init; }
    public string? FPago { get; init; }
    public decimal? PrimaNeta { get; init; }
    public decimal? PrimaTotal { get; init; }
}

public sealed class PolizaDetalleSICAS
{
    public int? IDDocto { get; init; }
    public string? Documento { get; init; }
    public string? Inciso { get; init; }
    public string? Serie { get; init; }
    public string? Motor { get; init; }
    public string? Placas { get; init; }
    public string? Marca { get; init; }
    public string? Tipo { get; init; }
    public string? Modelo { get; init; }
    public string? Color { get; init; }
    public string? UsoVehiculo { get; init; }
    public string? Servicio { get; init; }
    public int? Ocupantes { get; init; }
    public string? EqEsp { get; init; }
    public decimal? EqEspSAseg { get; init; }
    public string? Adap { get; init; }
    public decimal? AdapSAseg { get; init; }
}

public sealed class PolizaPrimasSICAS
{
    public int? IDDocto { get; init; }
    public string? Documento { get; init; }
    public string? Inciso { get; init; }
    public string? CiaAbreviacion { get; init; }
    public string? NombreCompleto { get; init; }
    public string? RFC { get; init; }
    public string? CURP { get; init; }
    public string? Email { get; init; }
    public string? Telefono { get; init; }
    public string? Domicilio { get; init; }
    public string? ColoniaDomicilio { get; init; }
    public string? CPDomicilio { get; init; }
    public string? Beneficiario { get; init; }
    public string? VendAbreviacion { get; init; }
    public string? FPago { get; init; }
    public string? SRamoNombre { get; init; }
    public string? FDesde { get; init; }
    public string? FHasta { get; init; }
    public string? FCaptura { get; init; }
    public decimal? PrimaNeta { get; init; }
    public decimal? PrimaTotal { get; init; }
    public decimal? Derechos { get; init; }
    public decimal? Recargos { get; init; }
    public decimal? IVA { get; init; }
    public decimal? PrimaPend { get; init; }
    public decimal? Comision { get; init; }
    public string? EjecutAbreviacion { get; init; }
    public string? EjecutNombre { get; init; }
    public string? CCobro_TXT { get; init; }
}

public sealed class PolizaCoberturasSICAS
{
    public int? IDCobertura { get; init; }
    public string? Nombre { get; init; }
    public string? SumaAseg { get; init; }
    public string? DeducibleL { get; init; }
    public string? DeducibleE { get; init; }
    public string? Comentario { get; init; }
    public decimal? PrimaNeta { get; init; }
}

public sealed class PolizaCobranzaSICAS
{
    public int? IDRecibo { get; init; }
    public string? FDesde { get; init; }
    public string? FHasta { get; init; }
    public string? FLimPago { get; init; }
    public decimal? PrimaNeta { get; init; }
    public decimal? PrimaTotal { get; init; }
    public decimal? PrimaPend { get; init; }
    public string? Status_TXT { get; init; }
}

public sealed class DatosPoliza
{
    public string NumeroPoliza { get; init; } = string.Empty;
    public string? Aseguradora { get; init; }
    public string? Beneficiario { get; init; }
    public string? BeneficiarioPreferente { get; init; }
    public string? Broker { get; init; }
    public string? FormaPago { get; init; }
    /// <summary>Usado únicamente para resolver EMP_ID contra el catálogo EMPRESAS; no se persiste como texto.</summary>
    public string? Contratante { get; init; }
}

public sealed class DatosVehiculo
{
    public string NumeroPoliza { get; init; } = string.Empty;
    public string? Serie { get; init; }
    public int? Inciso { get; init; }
    public string? TipoPoliza { get; init; }
    public DateTime? FechaInicio { get; init; }
    public DateTime? FechaVencimiento { get; init; }
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
    public string? CoberturasDanosMateriales { get; init; }
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
    /// <summary>"SUSTITUCIÓN" o "RENOVACIÓN" — decide la rama de reasignación de estatus.</summary>
    public string TipoActivo { get; init; } = "SUSTITUCIÓN";
    public List<string> Documentos { get; init; } = [];
}
