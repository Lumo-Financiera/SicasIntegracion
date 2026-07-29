using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;

namespace LumoSys.Integraciones.Domain.Seguros.Interfaces;

public interface ISeguroSICASClient
{
    Task<List<PolizaResumenSICAS>> BuscarPolizasVigentes(DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default);
    Task<PolizaDetalleSICAS?> BuscarDetalle(int idDocto, CancellationToken ct = default);
    Task<PolizaPrimasSICAS?> BuscarPrimas(int idDocto, CancellationToken ct = default);
    Task<List<PolizaCoberturasSICAS>> BuscarCoberturas(int idDocto, CancellationToken ct = default);
    Task<List<PolizaCobranzaSICAS>> BuscarCobranza(int idDocto, CancellationToken ct = default);
    Task<List<ArchivoSICAS>> BuscarDigital(long idDocto, CancellationToken ct = default);
    Task<PolizaDetalleSICAS?> BuscarDetallePorSerie(string serie, CancellationToken ct = default);
    Task<int?> BuscarIdDoctoporDocumento(string documento, CancellationToken ct = default);
    Task<PolizaResumenSICAS?> BuscarPolizaPorIdDocto(int idDocto, CancellationToken ct = default);
}
