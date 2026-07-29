namespace LumoSys.Integraciones.Domain.Siniestros.Models;

public sealed class SiniestroResumenSICAS
{
    public int? IDDocto { get; init; }
    public int? IDSiniestro { get; init; }
    /// <summary>SIN_NO_SINIESTRO viene de aquí (NumSiniestro), NO de IDSiniestro.</summary>
    public string? NumSiniestro { get; init; }
    public string? NumReporte { get; init; }
    public string? ClaveBit { get; init; }
    /// <summary>Documento = número de póliza (HDS00009 no trae serie; la serie real sale de
    /// una llamada aparte a HWS_DDETAIL, ver PolizaDetalleSICASS.Serie).</summary>
    public string? Documento { get; init; }
    /// <summary>CobAfectada: texto crudo, se normaliza con ObtenerTipoSiniestro antes de resolver el catálogo.</summary>
    public string? CobAfectada { get; init; }
    /// <summary>FPSintoma — fecha del evento/síntoma inicial.</summary>
    public string? FPSintoma { get; init; }
    public string? FCaptura { get; init; }
    public string? Descripcion { get; init; }
    public string? EjecutNombre { get; init; }
    public string? Status_Txt { get; init; }
    /// <summary>FStatus — fecha de resolución del siniestro.</summary>
    public string? FStatus { get; init; }
    public string? Inciso { get; init; }
}

public sealed class PolizaDetalleSICASS
{
    public int? IDDocto { get; init; }
    public string? Documento { get; init; }
    public string? Serie { get; init; }
    public string? Inciso { get; init; }
}

public sealed class SiniestroBitacoraSICAS
{
    public string? ClaveBit { get; init; }
    public string? Estatus { get; init; }
    public string? Comentarios { get; init; }
    public string? FechaRegistro { get; init; }
    public string? FechaEvento { get; init; }
    public int? IsAutom { get; init; }
    public string? Ejecutivo { get; init; }
    public int? IdUser { get; init; }
    public string? NumReporte { get; init; }
}

public sealed class DatosSiniestro
{
    public string NumeroPoliza { get; init; } = string.Empty;
    public string? NoSerie { get; init; }
    /// <summary>Ya normalizado (ObtenerTipoSiniestro) — listo para resolver contra TIPOS_SINIESTROS.</summary>
    public string? TipoSiniestro { get; init; }
    public DateTime? FechaEvento { get; init; }
    public string? Descripcion { get; init; }
    public string? NoSiniestro { get; init; }
    public string? NoReporte { get; init; }
    /// <summary>Siempre "SISTEMA" — literal fijo del ETL legacy, no viene de SICAS.</summary>
    public string TipoOrigen { get; init; } = "SISTEMA";
    public DateTime? FechaResolucion { get; init; }
    /// <summary>Regla legacy: 0 si TipoSiniestro contiene "ROBO", null en cualquier otro caso.</summary>
    public decimal? MontoIndemnizable { get; init; }
    /// <summary>Siempre null — el ETL legacy nunca lo llena pese a existir el campo.</summary>
    public decimal? MontoDeducible { get; init; }
    public decimal? MontoPrimasPendientes { get; init; }
    public decimal? MontoOtrosDescuentos { get; init; }
    public int? Inciso { get; init; }
}

public sealed class DatosEstatus
{
    public string? Estatus { get; init; }
    public string? Comentarios { get; init; }
    public DateTime? FechaEvento { get; init; }
    public DateTime? FechaRegistro { get; init; }
    public int IdUser { get; init; } = 3;
    public string? NumReporte { get; init; }
    public string? Ejecutivo { get; init; }
}
