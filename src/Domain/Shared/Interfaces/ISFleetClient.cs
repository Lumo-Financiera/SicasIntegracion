using LumoSys.Integraciones.Domain.Seguros.Models;

namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public interface ISFleetClient
{
    Task<int?> BuscarVehiculo(string serie, CancellationToken ct = default);
    Task<int?> BuscarPoliza(string numeroPoliza, CancellationToken ct = default);
    Task<int> GuardarPoliza(SolicitudSFleet solicitud, bool esEdicion, int vehiculoId, CancellationToken ct = default);
    Task<bool> SubirDocumento(int polizaId, int vehiculoId, string nombreArchivo, byte[] bytes, CancellationToken ct = default);
}
