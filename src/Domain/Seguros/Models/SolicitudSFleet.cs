namespace LumoSys.Integraciones.Domain.Seguros.Models;

/// <summary>
/// Datos de la póliza tal como los espera SFleet.
///
/// Hasta octubre de 2026 esta clase traía 11 propiedades y el cliente solo mandaba 8 campos, de
/// los ~40 que el ETL legacy (C:\LumoSysGit\Seguros) enviaba. SFleet rechazaba el alta con 422
/// por campos faltantes, de forma que NINGUNA póliza llegaba a SFleet y, en consecuencia, ningún
/// documento podía adjuntarse a ella. El contrato se reconstruyó tomando el payload del legacy
/// como referencia, que es el único que se sabe que SFleet aceptó en producción.
/// </summary>
public sealed class SolicitudSFleet
{
    // ─── Identificación ───────────────────────────────────────────────────────
    public string NumeroPoliza { get; init; } = string.Empty;
    public int ClienteVehiculoId { get; init; }
    public string? Aseguradora { get; init; }

    // ─── Vigencia ─────────────────────────────────────────────────────────────
    public DateTime FechaInicio { get; init; }
    public DateTime FechaVencimiento { get; init; }

    // ─── Partes ───────────────────────────────────────────────────────────────
    public string? Beneficiario { get; init; }
    public string? BeneficiarioPreferente { get; init; }
    public string? Broker { get; init; }
    public string? Ejecutivo { get; init; }
    public string? AdministracionCartera { get; init; }

    // ─── Condiciones ──────────────────────────────────────────────────────────
    public string? Cobertura { get; init; }
    public string? TipoPoliza { get; init; }
    public string? TipoUso { get; init; }
    public string? Pasajeros { get; init; }
    public int? Inciso { get; init; }

    // ─── Importes ─────────────────────────────────────────────────────────────
    public decimal PrimaNeta { get; init; }
    public decimal PrimaTotal { get; init; }
    public decimal? Derechos { get; init; }
    public decimal? Recargos { get; init; }
    public decimal? IVA { get; init; }
    public decimal? PrimerRecibo { get; init; }
    public decimal? Subsecuente { get; init; }

    // ─── Pago ─────────────────────────────────────────────────────────────────
    public string? FormaPago { get; init; }
    public string? GestionPago { get; init; }

    // ─── Activo ───────────────────────────────────────────────────────────────
    public string? Adaptacion { get; init; }
    public decimal? ValorAdaptacion { get; init; }
    public decimal? ValorFactura { get; init; }
    public string? ResponsabilidadCivilAdicionales { get; init; }

    /// <summary>Días de vigencia; SFleet los recibe calculados, igual que en el ETL legacy.</summary>
    public int Dias => FechaVencimiento > FechaInicio
        ? (FechaVencimiento - FechaInicio).Days
        : 0;
}
