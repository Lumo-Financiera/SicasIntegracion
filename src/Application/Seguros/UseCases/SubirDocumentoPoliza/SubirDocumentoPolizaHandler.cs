using System.IO;
using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;

public sealed class SubirDocumentoPolizaHandler(
    IPolizaRepository repo,
    IDocumentService documentService,
    ILogger<SubirDocumentoPolizaHandler> log)
{
    public async Task<int> Handle(SubirDocumentoPolizaCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Bytes.Length == 0)
        {
            log.LogWarning("SubirDocumentoPoliza: bytes vacíos para serie {Serie}", cmd.Serie);
            return 0;
        }

        bool yaExiste = await repo.ExisteDocumentoAsync(cmd.Serie, cmd.NombreArchivo, ct);
        if (yaExiste)
        {
            log.LogInformation("Documento {Archivo} ya existe para serie {Serie}", cmd.NombreArchivo, cmd.Serie);
            return 1;
        }

        int archivoId = await repo.RegistrarArchivoAsync(cmd.NombreArchivo, cmd.Bytes.Length, ct);
        if (archivoId <= 0)
        {
            log.LogError("No se pudo generar ARC_ID para el documento {Archivo}", cmd.NombreArchivo);
            return 0;
        }

        string extension = Path.GetExtension(cmd.NombreArchivo);
        bool subido = await documentService.SubirDocumentoPoliza($"{archivoId}{extension}", cmd.Bytes, ct);
        if (!subido)
        {
            log.LogError("Error subiendo al FTP el documento {Archivo} (ARC_ID={ArcId})", cmd.NombreArchivo, archivoId);
            return 0;
        }

        await repo.VincularDocumentoUnidadAsync(cmd.Serie, archivoId, ct);
        return archivoId;
    }
}
