namespace LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;

/// <summary>
/// Procesamiento de pólizas desde SICAS hacia LumoSys + SFleet.
/// Modos:
///   - Sin parámetros: lote automático (ayer → hoy).
///   - Poliza: fuerza reprocesamiento de una póliza por número de documento.
///   - Serie: fuerza reprocesamiento de una póliza por número de serie VIN.
///   - Desde/Hasta: lote en rango de fechas específico.
/// </summary>
public sealed class ProcesarLoteSeguroCommand
{
    public DateTime? Desde { get; init; }
    public DateTime? Hasta { get; init; }

    /// Número de póliza (folio) para reprocesar individualmente
    public string? Poliza { get; init; }

    /// Número de serie VIN del vehículo para reprocesar individualmente
    public string? Serie { get; init; }
}
