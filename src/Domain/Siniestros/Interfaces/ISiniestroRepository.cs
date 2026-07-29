using LumoSys.Integraciones.Domain.Siniestros.Models;

namespace LumoSys.Integraciones.Domain.Siniestros.Interfaces;

public interface ISiniestroRepository
{
    /// <summary>true si la serie existe en COMPRAS_DETALLES (con estatus válido) — pertenece a la
    /// flotilla propia. SIN_CDE_ID es NOT NULL y no tiene equivalente a VEHICULOS (a diferencia de
    /// Seguros), así que un vehículo "externo" no puede tener siniestro registrado.</summary>
    Task<bool> ExisteVehiculoAsync(string serie, CancellationToken ct = default);

    Task<int> UpsertSiniestroAsync(DatosSiniestro datos, CancellationToken ct = default);
    Task<bool> ExisteEstatusAsync(int siniestroId, string comentarios, DateTime fechaRegistro, CancellationToken ct = default);
    Task UpsertEstatusAsync(DatosEstatus datos, int siniestroId, CancellationToken ct = default);
    Task<bool> ExisteDocumentoAsync(int siniestroId, string nombreBase, CancellationToken ct = default);
    Task RegistrarDocumentoAsync(int siniestroId, string nombreArchivo, CancellationToken ct = default);
    Task<int?> BuscarIdPorReporte(string noReporte, CancellationToken ct = default);
}
