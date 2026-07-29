namespace LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;

/// <summary>
/// Procesamiento de siniestros desde SICAS hacia LumoSys.
/// Modos:
///   - Sin parámetros: lote automático (ayer → hoy) + bitácora del día.
///   - FolioSiniestro: reprocesa un siniestro específico por número de reporte.
///   - Desde/Hasta: lote en rango de fechas específico.
/// </summary>
public sealed class ProcesarLoteSiniestroCommand
{
    public DateTime? Desde { get; init; }
    public DateTime? Hasta { get; init; }

    /// Número de reporte del siniestro para reprocesar individualmente (ej: 1-202-2026-R-4295)
    public string? FolioSiniestro { get; init; }
}
