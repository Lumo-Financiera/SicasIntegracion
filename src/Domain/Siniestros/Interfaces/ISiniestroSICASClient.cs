using LumoSys.Integraciones.Domain.Siniestros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;

namespace LumoSys.Integraciones.Domain.Siniestros.Interfaces;

public interface ISiniestroSICASClient
{
    Task<List<SiniestroResumenSICAS>> BuscarSiniestrosVigentes(DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default);
    Task<PolizaDetalleSICASS?> BuscarDetalle(int idDocto, CancellationToken ct = default);
    Task<List<SiniestroBitacoraSICAS>> BuscarBitacora(string claveBit, CancellationToken ct = default);
    Task<List<SiniestroBitacoraSICAS>> BuscarBitacoraPorFecha(DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default);
    Task<List<SiniestroResumenSICAS>> BuscarPorReporte(string numReporte, CancellationToken ct = default);
    Task<List<ArchivoSICAS>> BuscarDigital(long idSiniestro, CancellationToken ct = default);
}
