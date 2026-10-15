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

    /// <summary>
    /// Sincroniza los documentos del Centro Digital de SICAS hacia LumoSys, sin tocar siniestros
    /// ni estatus.
    ///
    /// Es el mismo trabajo que corre solo al arrancar el servicio; este endpoint existe para poder
    /// repetirlo bajo demanda —por ejemplo, en cuanto se corrija el acceso al FTP— sin reiniciar.
    ///
    /// <paramref name="completa"/> en true recorre TODOS los siniestros vinculados a SICAS; en
    /// false solo los que no tienen ningún documento, los abiertos y los recientes. En ninguno de
    /// los dos modos se duplica nada: cada archivo se consulta antes contra DOCUMENTOS_SINIESTROS.
    ///
    /// ⚠️ Una pasada completa puede tardar más que el timeout del cliente que la invoque; si la
    /// conexión se cierra, el proceso se corta a medias en el servidor (ver la nota operativa sobre
    /// rangos largos en CLAUDE.md). Conviene invocarlo con un timeout de cliente generoso.
    /// </summary>
    [HttpPost("Siniestros/Documentos")]
    public async Task<IActionResult> SincronizarDocumentosSiniestros(
        [FromQuery] bool completa, CancellationToken ct)
    {
        await siniestrosHandler.Handle(new ProcesarLoteSiniestroCommand
        {
            SoloDocumentos   = true,
            RevisionCompleta = completa
        }, ct);

        return Ok(new
        {
            Estatus = true,
            Mensaje = $"Sincronización documental ({(completa ? "completa" : "incremental")}) finalizada. " +
                      "El detalle (documentos recuperados, fallos) queda en el log y en Sentry."
        });
    }
}
