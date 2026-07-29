using LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;
using LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;
using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace LumoSys.Integraciones.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SegurosController(
    GuardarPolizaHandler guardar,
    SubirDocumentoPolizaHandler subir,
    IPolizaRepository repo) : ControllerBase
{
    [HttpPost("Guardar")]
    public async Task<IActionResult> Guardar(
        [FromBody] GuardarPolizaCommand cmd, CancellationToken ct)
    {
        var resultado = await guardar.Handle(cmd, ct);
        return resultado.Exitoso
            ? Ok(new { Estatus = true, resultado.PolizaId, resultado.VehiculoId })
            : BadRequest(new { Estatus = false, resultado.Mensaje });
    }

    [HttpPost("SubirPoliza/{serie}")]
    public async Task<IActionResult> SubirPoliza(
        string serie, IFormFile archivo, CancellationToken ct)
    {
        if (archivo is null || archivo.Length == 0)
            return BadRequest(new { Estatus = false, Mensaje = "Archivo requerido." });

        using var ms = new MemoryStream();
        await archivo.CopyToAsync(ms, ct);

        var cmd = new SubirDocumentoPolizaCommand
        {
            Serie         = serie,
            NombreArchivo = archivo.FileName,
            Bytes         = ms.ToArray()
        };

        int documentoId = await subir.Handle(cmd, ct);
        return Ok(new { Estatus = documentoId > 0, DocumentoUnidad = documentoId });
    }

    [HttpGet("BuscarPolizaCargada")]
    public async Task<IActionResult> BuscarPolizaCargada(
        [FromQuery] string poliza, [FromQuery] string inciso, CancellationToken ct)
    {
        bool existe = await repo.ExisteDocumentoAsync(poliza, inciso, ct);
        return Ok(new { Estatus = true, Existe = existe });
    }
}
