using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;

namespace LumoSys.Integraciones.Infrastructure.SICAS;

public sealed class SICASSiniestroClient(ISICASRestClient sicas) : ISiniestroSICASClient
{
    // SICAS espera fechas en formato dd/MM/yyyy en los filtros de Conditions (mismo hallazgo
    // que en SICASSeguroClient, confirmado contra datos reales de FCaptura).
    private const string DateFmt = "dd/MM/yyyy";

    public async Task<List<SiniestroResumenSICAS>> BuscarSiniestrosVigentes(
        DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default)
    {
        // Hasta es exclusivo del día indicado en el filtro de rango de SICAS (confirmado
        // empíricamente: "27/07→28/07" no trae nada capturado el 28/07) — se suma 1 día.
        string haciaFmt = hasta.Date.AddDays(1).ToString(DateFmt);

        var resp = await sicas.ReadData<SiniestroResumenSICAS>(new SolicitudReadData
        {
            KeyCode     = "HDS00009",
            Page        = pagina,
            ItemForPage = 100,
            InfoSort    = "DatSiniestros.IDDocto-",
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "Desde|Hasta",
                    FilterType = 3,
                    Values     = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    Texts      = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    ColumnName = "DatSiniestros.FCaptura"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public async Task<PolizaDetalleSICASS?> BuscarDetalle(int idDocto, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<PolizaDetalleSICASS>(new SolicitudReadData
        {
            KeyCode     = "HWS_DDETAIL",
            Page        = 1,
            ItemForPage = 5,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "IDDocto",
                    FilterType = 0,
                    Values     = idDocto.ToString(),
                    ColumnName = "DatDocumentos.IDDocto"
                }
            ]
        }, ct);

        return resp?.FirstOrDefault();
    }

    public async Task<List<SiniestroBitacoraSICAS>> BuscarBitacora(string claveBit, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<SiniestroBitacoraSICAS>(new SolicitudReadData
        {
            KeyCode     = "H04270_O",
            Page        = 1,
            ItemForPage = 200,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "ClaveBit",
                    FilterType = 0,
                    Values     = claveBit,
                    ColumnName = "DatBitacora.ClaveBit"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public async Task<List<SiniestroBitacoraSICAS>> BuscarBitacoraPorFecha(
        DateTime desde, DateTime hasta, int pagina, CancellationToken ct = default)
    {
        // Hasta exclusivo del día indicado (mismo hallazgo que en BuscarSiniestrosVigentes) —
        // se suma 1 día. El legacy original ya lo sabía: usaba literalmente "hoy|mañana".
        string haciaFmt = hasta.Date.AddDays(1).ToString(DateFmt);

        var resp = await sicas.ReadData<SiniestroBitacoraSICAS>(new SolicitudReadData
        {
            KeyCode     = "H03314011",
            Page        = pagina,
            ItemForPage = 1000,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "Desde|Hasta",
                    FilterType = 3,
                    Values     = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    Texts      = $"{desde.ToString(DateFmt)}|{haciaFmt}",
                    ColumnName = "DatBitacora.FechaHora"
                },
                new CondicionSICAS
                {
                    Label      = "Reclamaciones",
                    FilterType = 0,
                    Values     = "Reclamaciones",
                    ColumnName = "DatBitacora.Procedencia"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public async Task<List<SiniestroResumenSICAS>> BuscarPorReporte(string numReporte, CancellationToken ct = default)
    {
        var resp = await sicas.ReadData<SiniestroResumenSICAS>(new SolicitudReadData
        {
            KeyCode     = "HDS00009",
            Page        = 1,
            ItemForPage = 5,
            Conditions  =
            [
                new CondicionSICAS
                {
                    Label      = "NumReporte",
                    FilterType = 0,
                    Values     = numReporte,
                    ColumnName = "NumReporte"
                }
            ]
        }, ct);

        return resp ?? [];
    }

    public Task<List<ArchivoSICAS>> BuscarDigital(long idSiniestro, CancellationToken ct = default) =>
        sicas.BuscarArchivosDigitales("H04", idSiniestro, ct);
}
