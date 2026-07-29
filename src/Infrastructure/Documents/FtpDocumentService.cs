using FluentFTP;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Infrastructure.Documents;

public sealed class FtpOptions
{
    public string Host { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Contrasena { get; init; } = string.Empty;
    public string RutaDestinoPolizas { get; init; } = "Fleet/Documentos Unidades/2/0/";
    public string RutaDestinoSiniestros { get; init; } = "Seguros/Siniestros/";
}

public sealed class FtpDocumentService(
    IOptions<FtpOptions> opts,
    ILogger<FtpDocumentService> log) : IDocumentService
{
    private readonly FtpOptions _opts = opts.Value;

    public async Task<bool> SubirDocumentoPoliza(
        string nombreArchivoRemoto, byte[] bytes, CancellationToken ct = default)
    {
        int status = await SubirArchivo(_opts.RutaDestinoPolizas, nombreArchivoRemoto, bytes, ct);
        return status > 0;
    }

    public async Task<string?> SubirDocumentoSiniestro(
        string nombreArchivo, byte[] bytes, CancellationToken ct = default)
    {
        string nombreFinal = $"{Path.GetFileNameWithoutExtension(nombreArchivo)}_3_{DateTime.Now:ddMMyyHHmmss}{Path.GetExtension(nombreArchivo)}";
        int status = await SubirArchivo(_opts.RutaDestinoSiniestros, nombreFinal, bytes, ct);
        return status > 0 ? nombreFinal : null;
    }

    private async Task<int> SubirArchivo(string rutaDestino, string nombreArchivo, byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var ftp = new AsyncFtpClient(_opts.Host, _opts.Usuario, _opts.Contrasena);
            ftp.Config.EncryptionMode = FtpEncryptionMode.None;

            await ftp.Connect(ct);
            await ftp.SetWorkingDirectory(rutaDestino, ct);

            using var ms = new MemoryStream(bytes);
            var status = await ftp.UploadStream(ms, nombreArchivo, FtpRemoteExists.Skip, token: ct);

            if (status == FtpStatus.Success || status == FtpStatus.Skipped)
            {
                log.LogInformation("FTP: {Archivo} subido a {Ruta}", nombreArchivo, rutaDestino);
                return 1;
            }

            log.LogWarning("FTP: {Archivo} no se subió (estado {Status})", nombreArchivo, status);
            return 0;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error FTP subiendo {Archivo}", nombreArchivo);
            return 0;
        }
    }
}
