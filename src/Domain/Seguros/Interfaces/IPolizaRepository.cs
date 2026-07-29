using LumoSys.Integraciones.Domain.Seguros.Models;

namespace LumoSys.Integraciones.Domain.Seguros.Interfaces;

public interface IPolizaRepository
{
    /// <summary>true si la serie existe en COMPRAS_DETALLES (con estatus válido) o en VEHICULOS —
    /// es decir, si pertenece a la flotilla propia. Úsese antes de UpsertPolizaAsync/UpsertVehiculoAsync
    /// para evitar crear registros huérfanos/duplicados para vehículos ajenos.</summary>
    Task<bool> ExisteVehiculoAsync(string serie, CancellationToken ct = default);

    Task<int> UpsertPolizaAsync(DatosPoliza datos, CancellationToken ct = default);
    Task<int> UpsertVehiculoAsync(DatosVehiculo datos, int polizaId, CancellationToken ct = default);
    Task<bool> ExisteDocumentoAsync(string serie, string nombreArchivo, CancellationToken ct = default);

    /// <summary>Crea la fila real en ARCHIVOS_REPOSITORIOS (ARC_ID vía SP_ACTUALIZAR_SECUENCIAS). Retorna el ARC_ID real.</summary>
    Task<int> RegistrarArchivoAsync(string nombreArchivo, long tamanoBytes, CancellationToken ct = default);

    /// <summary>Vincula un ARC_ID ya subido al FTP con el vehículo (por serie) en DOCUMENTOS_UNIDADES.</summary>
    Task VincularDocumentoUnidadAsync(string serie, int archivoId, CancellationToken ct = default);

    Task<int> ObtenerSecuenciaAsync(string tabla, CancellationToken ct = default);
}
