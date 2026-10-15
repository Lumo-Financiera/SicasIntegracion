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

    /// <summary>
    /// Obtiene el contenido de los archivos pedidos, por la vía que funcione.
    ///
    /// SICAS publica los documentos en un host DISTINTO al de la API: la API vive en
    /// <c>security-services.sicasonline.info</c> y los PDF en <c>www.sicasonline.info</c> (misma IP,
    /// 201.150.37.147, pero otro nombre). El servidor de producción resuelve el primero y **no** el
    /// segundo: Sentry lo reportó el 14/10/2026 como <c>HttpRequestException: Host desconocido
    /// (www.sicasonline.info:443)</c> desde <c>DownloadFile</c>. Mientras ese DNS no se corrija,
    /// ningún documento se puede descargar por URL.
    ///
    /// Por eso existe el respaldo: <c>GetFiles</c> con <c>URLSecurity=1</c> devuelve el archivo
    /// completo en base64 dentro de la propia respuesta de la API, por el host que producción sí
    /// resuelve. Verificado contra SICAS real el 15/10/2026 — los tres PDF del siniestro 35635
    /// decodifican con cabecera <c>%PDF-</c> y tamaños idénticos a los que anuncia el metadato.
    ///
    /// Devuelve un diccionario nombre-de-archivo → contenido. Los que no se pudieron obtener
    /// simplemente no aparecen.
    /// </summary>
    Task<Dictionary<string, byte[]>> ObtenerContenidos(
        string identity, long valuePK, IReadOnlyCollection<ArchivoSICAS> archivos, CancellationToken ct = default);
}
