using LumoSys.Integraciones.Domain.Seguros.Models;

namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public interface ISFleetClient
{
    Task<int?> BuscarVehiculo(string serie, CancellationToken ct = default);
    Task<int?> BuscarPoliza(string numeroPoliza, CancellationToken ct = default);
    /// <summary>
    /// Da de alta o actualiza la póliza en SFleet. <paramref name="polizaSFleetId"/> es el id que
    /// SFleet asignó a la PÓLIZA (el que devuelve BuscarPoliza): con valor hace PATCH sobre ella y
    /// en null da de alta. El vehículo no va en la ruta, viaja en el cuerpo como client_car_id.
    /// </summary>
    Task<int> GuardarPoliza(SolicitudSFleet solicitud, int? polizaSFleetId, CancellationToken ct = default);
    Task<bool> SubirDocumento(int polizaId, int vehiculoId, string nombreArchivo, byte[] bytes, CancellationToken ct = default);
}
