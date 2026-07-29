namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public interface ISICASRestClient
{
    Task<List<T>?> ReadData<T>(SolicitudReadData solicitud, CancellationToken ct = default) where T : class;
    /// <summary>Recupera los archivos digitales de una entidad (póliza, siniestro, etc.) desde el
    /// Centro Digital de SICAS vía /DigitalCenter/GetFiles (NO GetFilesAdv — ese depende de una
    /// configuración especial por agente/corredor que no está dada de alta para esta licencia y
    /// truena con "Internal error server").</summary>
    Task<List<ArchivoSICAS>> BuscarArchivosDigitales(string identity, long valuePK, CancellationToken ct = default);
    Task<byte[]?> DownloadFile(string fileUrl, CancellationToken ct = default);
}
