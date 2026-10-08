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

    /// <summary>Trae un siniestro por su IDSiniestro. Lo usa la reconciliación: un mismo folio
    /// puede corresponder a varios siniestros, así que buscar por NumReporte no sirve ahí.</summary>
    Task<SiniestroResumenSICAS?> BuscarPorIdSiniestro(int idSiniestro, CancellationToken ct = default);

    /// <summary>Historial de bitácora de UN siniestro, sin depender del rango de fechas.</summary>
    Task<List<SiniestroBitacoraSICAS>> BuscarBitacoraPorSiniestro(int idSiniestro, CancellationToken ct = default);

    Task<List<ArchivoSICAS>> BuscarDigital(long idSiniestro, CancellationToken ct = default);
}
