using LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LumoSys.Integraciones.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SiniestrosController(
    GuardarSiniestroHandler guardar,
    ISiniestroRepository repo,
    IDocumentService documentService) : ControllerBase
{
    [HttpPost("Guardar")]
    public async Task<IActionResult> Guardar(
        [FromBody] GuardarSiniestroCommand cmd, CancellationToken ct)
    {
        var resultado = await guardar.Handle(cmd, ct);
        return resultado.Exitoso
            ? Ok(new { Estatus = true, resultado.SiniestroId })
            : BadRequest(new { Estatus = false, resultado.Mensaje });
    }

    [HttpPost("GuardarComentario")]
    public async Task<IActionResult> GuardarComentario(
        [FromBody] EstatusCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.NumReporte))
            return BadRequest(new { Estatus = false, Mensaje = "NumReporte requerido." });

        int? siniestroId = await repo.BuscarIdPorReporte(cmd.NumReporte, ct);
        if (siniestroId is null)
            return NotFound(new { Estatus = false, Mensaje = $"Siniestro {cmd.NumReporte} no encontrado." });

        var datos = new LumoSys.Integraciones.Domain.Siniestros.Models.DatosEstatus
        {
            Estatus       = cmd.Estatus,
            Comentarios   = cmd.Comentarios,
            FechaEvento   = DateTime.TryParse(cmd.FechaEvento, out var fe) ? fe : null,
            FechaRegistro = DateTime.TryParse(cmd.FechaEstatus, out var fr) ? fr : null,
            IdUser        = cmd.IdUser,
            NumReporte    = cmd.NumReporte,
            Ejecutivo     = cmd.Ejecutivo
        };

        await repo.UpsertEstatusAsync(datos, siniestroId.Value, ct);
        return Ok(new { Estatus = true });
    }

    [HttpPost("SubirDocumentos/{reporte}")]
    public async Task<IActionResult> SubirDocumentos(
        string reporte, IFormFile archivo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0)
            return BadRequest(new { Estatus = false, Mensaje = "Archivo requerido." });

        string noReporte = reporte.Replace("|", "/");

        int? siniestroId = await repo.BuscarIdPorReporte(noReporte, ct);
        if (siniestroId is null)
            return NotFound(new { Estatus = false, Mensaje = $"Siniestro {noReporte} no encontrado." });

        string nombreBase = Path.GetFileNameWithoutExtension(archivo.FileName);
        bool existe = await repo.ExisteDocumentoAsync(siniestroId.Value, nombreBase, ct);
        if (existe)
            return Ok(new { Estatus = true, Mensaje = "Ya existe el documento.", DocumentoSiniestro = 0 });

        using var ms = new MemoryStream();
        await archivo.CopyToAsync(ms, ct);

        string? nombreFinal = await documentService.SubirDocumentoSiniestro(archivo.FileName, ms.ToArray(), ct);
        if (nombreFinal is not null)
            await repo.RegistrarDocumentoAsync(siniestroId.Value, nombreFinal, ct);

        return Ok(new { Estatus = nombreFinal is not null, DocumentoSiniestro = nombreFinal });
    }

    [HttpGet("BuscarSiniestroCargado")]
    public async Task<IActionResult> BuscarSiniestroCargado(
        [FromQuery] string noReporte, CancellationToken ct)
    {
        int? id = await repo.BuscarIdPorReporte(noReporte, ct);
        return Ok(new { Estatus = true, Existe = id.HasValue, SiniestroId = id });
    }

    [HttpGet("Estado")]
    public async Task<IActionResult> Estado([FromQuery] string noReporte, CancellationToken ct)
    {
        int? id = await repo.BuscarIdPorReporte(noReporte, ct);
        return Ok(new { Estatus = true, SiniestroId = id });
    }
}
