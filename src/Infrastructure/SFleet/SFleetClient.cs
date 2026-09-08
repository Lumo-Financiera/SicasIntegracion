using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RestSharp;
using RestSharp.Authenticators;

namespace LumoSys.Integraciones.Infrastructure.SFleet;

public sealed class SFleetOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public sealed class SFleetClient(
    IOptions<SFleetOptions> opts,
    IMonitoreoErrores monitoreo,
    ILogger<SFleetClient> log) : ISFleetClient, IDisposable
{
    private readonly RestClient _http = new(opts.Value.BaseUrl);
    private readonly SFleetOptions _opts = opts.Value;
    private string? _token;

    private async Task<string?> ObtenerToken(CancellationToken ct)
    {
        if (_token is not null) return _token;

        var req = new RestRequest("authentications", Method.Post);
        req.AddParameter("email", _opts.Email);
        req.AddParameter("password", _opts.Password);

        var resp = await _http.ExecuteAsync(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null)
        {
            // Antes se devolvía null sin dejar registro: con las credenciales caídas, cada póliza
            // salía como "serie no encontrada en SFleet" (un aviso normal) y nada indicaba que en
            // realidad ninguna se estaba sincronizando.
            log.LogError("No se pudo autenticar contra SFleet: {Status}", resp.StatusCode);
            monitoreo.RastrearFallo("sfleet.token", "Autenticación rechazada",
                ("status", resp.StatusCode.ToString()), ("email", _opts.Email));
            return null;
        }

        var json = Newtonsoft.Json.Linq.JObject.Parse(resp.Content);
        _token = json["access_token"]?.ToString();
        return _token;
    }

    public async Task<int?> BuscarVehiculo(string serie, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return null;

        var req = new RestRequest("client_cars");
        req.AddHeader("Authorization", $"Bearer {token}");
        req.AddQueryParameter("q", serie);

        var resp = await _http.ExecuteGetAsync(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null) return null;

        var arr = Newtonsoft.Json.Linq.JArray.Parse(resp.Content);
        return arr.FirstOrDefault()?["id"]?.ToObject<int>();
    }

    public async Task<int?> BuscarPoliza(string numeroPoliza, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return null;

        var req = new RestRequest("request_insurances");
        req.AddHeader("Authorization", $"Bearer {token}");
        req.AddQueryParameter("q", numeroPoliza);
        req.AddQueryParameter("per_page", "500");

        var resp = await _http.ExecuteGetAsync(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null) return null;

        var arr = Newtonsoft.Json.Linq.JArray.Parse(resp.Content);
        return arr.FirstOrDefault()?["id"]?.ToObject<int>();
    }

    public async Task<int> GuardarPoliza(
        SolicitudSFleet solicitud, bool esEdicion, int vehiculoId, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return 0;

        var metodo = esEdicion ? Method.Patch : Method.Post;
        var ruta   = esEdicion ? $"request_insurances/{vehiculoId}" : "request_insurances";

        var req = new RestRequest(ruta, metodo);
        req.AddHeader("Authorization", $"Bearer {token}");
        req.AddJsonBody(new
        {
            request_insurance = new
            {
                police          = solicitud.NumeroPoliza,
                net_premium     = solicitud.PrimaNeta,
                coverage        = solicitud.Cobertura,
                date_start      = solicitud.FechaInicio.ToString("yyyy-MM-dd"),
                date_end        = solicitud.FechaVencimiento.ToString("yyyy-MM-dd"),
                client_car_id   = solicitud.ClienteVehiculoId,
                beneficiary     = solicitud.Beneficiario,
                broker          = solicitud.Broker,
                way_to_pay      = solicitud.FormaPago
            }
        });

        var resp = await _http.ExecuteAsync(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null)
        {
            log.LogWarning("SFleet GuardarPoliza {Poliza} falló: {Status}", solicitud.NumeroPoliza, resp.StatusCode);
            monitoreo.RastrearFallo("sfleet.guardar-poliza", $"SFleet rechazó la póliza {solicitud.NumeroPoliza}",
                ("status", resp.StatusCode.ToString()),
                ("operacion", esEdicion ? "edicion" : "alta"),
                ("vehiculo_id", vehiculoId.ToString()));
            return 0;
        }

        var json = Newtonsoft.Json.Linq.JObject.Parse(resp.Content);
        return json["id"]?.ToObject<int>() ?? 0;
    }

    public async Task<bool> SubirDocumento(
        int polizaId, int vehiculoId, string nombreArchivo, byte[] bytes, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return false;

        var req = new RestRequest($"client_cars/{vehiculoId}/files", Method.Post);
        req.AddHeader("Authorization", $"Bearer {token}");
        req.AddParameter("client_car_file[name]", nombreArchivo);
        req.AddParameter("client_car_file[serviable_type]", "RequestInsurance");
        req.AddParameter("client_car_file[serviable_id]", polizaId);
        req.AddParameter("client_car_file[documentation_car_id]", 2);
        req.AddFile("client_car_file[file]", bytes, nombreArchivo);

        var resp = await _http.ExecuteAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
