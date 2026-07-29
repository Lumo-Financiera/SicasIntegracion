using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using Microsoft.AspNetCore.Mvc;

namespace LumoSys.Integraciones.API.Controllers;

/// <summary>
/// Permite disparar el ETL bajo demanda o reprocesar una póliza/siniestro específico.
/// Útil cuando un registro no se migró correctamente y se necesita forzar el reprocesamiento.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class EtlController(
    ProcesarLoteSeguroHandler segurosHandler,
    ProcesarLoteSiniestroHandler siniestrosHandler) : ControllerBase
{
    /// <summary>
    /// Procesa pólizas de seguros desde SICAS.
    /// - Sin parámetros: lote automático (ayer → hoy).
    /// - Con Poliza: reprocesa una póliza por número de documento.
    /// - Con Serie: reprocesa una póliza por número de serie VIN.
    /// - Con Desde/Hasta: lote en rango específico.
    /// </summary>
    [HttpPost("Seguros/Procesar")]
    public async Task<IActionResult> ProcesarSeguros(
        [FromBody] ProcesarLoteSeguroCommand cmd, CancellationToken ct)
    {
        await segurosHandler.Handle(cmd, ct);
        return Ok(new { Estatus = true, Mensaje = "Procesamiento de seguros completado." });
    }

    /// <summary>
    /// Procesa siniestros desde SICAS.
    /// - Sin parámetros: lote automático (ayer → hoy) + bitácora del día.
    /// - Con FolioSiniestro: reprocesa un siniestro por número de reporte.
    /// - Con Desde/Hasta: lote en rango específico.
    /// </summary>
    [HttpPost("Siniestros/Procesar")]
    public async Task<IActionResult> ProcesarSiniestros(
        [FromBody] ProcesarLoteSiniestroCommand cmd, CancellationToken ct)
    {
        await siniestrosHandler.Handle(cmd, ct);
        return Ok(new { Estatus = true, Mensaje = "Procesamiento de siniestros completado." });
    }
}
