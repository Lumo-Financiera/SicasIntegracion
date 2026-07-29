using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;

namespace LumoSys.Integraciones.Infrastructure.SICAS;

public sealed class SICASSeguroClient(ISICASRestClient sicas) : ISeguroSICASClient
{
    // SICAS espera fechas en formato dd/MM/yyyy en los filtros de Conditions (confirmado
    // contra datos reales de FCaptura, ej. "18/04/2023 18:19:00"); yyyy-MM-dd no matchea nada.
    private const string DateFmt = "dd/MM/yyyy";

    public async Task<List<PolizaResumenSICAS>> BuscarPolizasVigentes(
        DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default)
    {
        // El límite superior del filtro de rango de SICAS (FilterType=3) es EXCLUSIVO del día
        // de "Hasta" — confirmado empíricamente: "27/07→28/07" NO trae nada capturado el 28/07,
        // solo "28/07→29/07" sí. Por eso se suma 1 día a Hasta al formatear (mismo patrón que
        // ya usaba el legacy BuscarBitacoraPorFecha con "hoy|mañana").
        string haciaFmt = hasta.Date.AddDays(1).ToString(DateFmt);

        var resp = await sicas.ReadData<PolizaResumenSICAS>(new SolicitudReadData
        {
            KeyCode    = "H03117",
            Page       = pagina,
            ItemForPage = 100,
            InfoSort   = "DatDocumentos.IDDocto-",
            Conditions =
            [
                new CondicionSICAS
                {
                    Label      = "Desde|Hasta",
                    FilterType = 3,
                    Values     = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    Texts      = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    ColumnName = "DatDocumentos.FCaptura"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public async Task<PolizaDetalleSICAS?> BuscarDetalle(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaDetalleSICAS>(new SolicitudReadData
        {
            KeyCode     = "HWS_DDETAIL",
            Page        = 1,
            ItemForPage = 10,
            InfoSort    = "DatDocumentos.IDDocto-",
            Conditions  = [FiltroIdDocto(idDocto)]
        }, ct);

        return resp?.FirstOrDefault();
    }

    public async Task<PolizaPrimasSICAS?> BuscarPrimas(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaPrimasSICAS>(new SolicitudReadData
        {
            KeyCode     = "H03400",
            Page        = 1,
            ItemForPage = 30,
            Conditions  = [FiltroIdDocto(idDocto)]
        }, ct);

        return resp?.FirstOrDefault();
    }

    public async Task<List<PolizaCoberturasSICAS>> BuscarCoberturas(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaCoberturasSICAS>(new SolicitudReadData
        {
            KeyCode     = "H03400_019",
            Page        = 1,
            ItemForPage = 20,
            Conditions  = [FiltroIdDocto(idDocto)]
        }, ct);

        return resp ?? [];
    }

    public async Task<List<PolizaCobranzaSICAS>> BuscarCobranza(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaCobranzaSICAS>(new SolicitudReadData
        {
            KeyCode     = "H03120",
            Page        = 1,
            ItemForPage = 80,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "IDDocto",
                    FilterType = 0,
                    Values     = idDocto.ToString(),
                    ColumnName = "VDatRecibos.IDDocto"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public Task<List<ArchivoSICAS>> BuscarDigital(long idDocto, CancellationToken ct = default) =>
        sicas.BuscarArchivosDigitales("H02", idDocto, ct);

    public async Task<PolizaDetalleSICAS?> BuscarDetallePorSerie(string serie, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaDetalleSICAS>(new SolicitudReadData
        {
            KeyCode     = "HWS_DDETAIL",
            Page        = 1,
            ItemForPage = 5,
            InfoSort    = "DatDocumentos.IDDocto-",
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "Serie",
                    FilterType = 0,
                    Values     = serie,
                    ColumnName = "DatDoctoDetail.Serie"
                }
            ]
        }, ct);

        return resp?.FirstOrDefault();
    }

    public async Task<int?> BuscarIdDoctoporDocumento(string documento, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaDetalleSICAS>(new SolicitudReadData
        {
            KeyCode     = "HWS_DDETAIL",
            Page        = 1,
            ItemForPage = 5,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "Documento",
                    FilterType = 0,
                    Values     = documento,
                    ColumnName = "DatDocumentos.Documento"
                }
            ]
        }, ct);

        return resp?.FirstOrDefault()?.IDDocto;
    }

    public async Task<PolizaResumenSICAS?> BuscarPolizaPorIdDocto(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaResumenSICAS>(new SolicitudReadData
        {
            KeyCode     = "H03117",
            Page        = 1,
            ItemForPage = 5,
            Conditions  = [FiltroIdDocto(idDocto)]
        }, ct);

        return resp?.FirstOrDefault();
    }

    private static CondicionSICAS FiltroIdDocto(int idDocto) => new()
    {
        Label      = "IDDocto",
        FilterType = 0,
        Values     = idDocto.ToString(),
        ColumnName = "DatDocumentos.IDDocto"
    };
}
