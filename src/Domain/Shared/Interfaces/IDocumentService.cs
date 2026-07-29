namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public interface IDocumentService
{
    Task<bool> SubirDocumentoPoliza(string nombreArchivoRemoto, byte[] bytes, CancellationToken ct = default);

    /// <summary>Sube el archivo con un nombre final generado internamente (patrón "{base}_3_{timestamp}{ext}").
    /// Retorna ese nombre final (para registrarlo en DOCUMENTOS_SINIESTROS) o null si falló la subida.</summary>
    Task<string?> SubirDocumentoSiniestro(string nombreArchivo, byte[] bytes, CancellationToken ct = default);
}
