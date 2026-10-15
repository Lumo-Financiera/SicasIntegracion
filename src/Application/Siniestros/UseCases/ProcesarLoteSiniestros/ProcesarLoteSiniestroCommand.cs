namespace LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;

/// <summary>
/// Procesamiento de siniestros desde SICAS hacia LumoSys.
/// Modos:
///   - Sin parámetros: lote automático (ayer → hoy) + bitácora del día.
///   - FolioSiniestro: reprocesa un siniestro específico por número de reporte.
///   - Desde/Hasta: lote en rango de fechas específico.
///   - SoloDocumentos: no toca siniestros ni estatus; únicamente sincroniza documentos (Fase 4).
/// </summary>
public sealed class ProcesarLoteSiniestroCommand
{
    public DateTime? Desde { get; init; }
    public DateTime? Hasta { get; init; }

    /// Número de reporte del siniestro para reprocesar individualmente (ej: 1-202-2026-R-4295)
    public string? FolioSiniestro { get; init; }

    /// <summary>
    /// Ejecuta ÚNICAMENTE la sincronización de documentos con el Centro Digital de SICAS, sin
    /// barrido de siniestros, sin bitácora y sin reconciliación de estatus. Es el modo que usa el
    /// servicio de sincronización documental al arrancar y el endpoint de disparo manual.
    /// </summary>
    public bool SoloDocumentos { get; init; }

    /// <summary>
    /// Con <see cref="SoloDocumentos"/>: revisa TODOS los siniestros vinculados a SICAS en vez de
    /// solo los abiertos, los que no tienen ningún documento y los recientes.
    ///
    /// Es la pasada de puesta al día que corre al desplegar: recupera lo que quedó atrás desde que
    /// el integrador arrancó, sin depender de la ventana de días. No duplica nada —cada archivo se
    /// consulta antes con <c>ExisteDocumentoAsync</c>—, solo cuesta una llamada a SICAS por
    /// siniestro ya completo.
    /// </summary>
    public bool RevisionCompleta { get; init; }
}
