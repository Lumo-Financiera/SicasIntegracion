using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace LumoSys.Integraciones.Infrastructure.SFleet;

public sealed class SFleetOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

/// <summary>
/// Cliente de SFleet. Todos sus métodos devuelven <c>null</c>/<c>0</c>/<c>false</c> ante un fallo en
/// vez de lanzar, porque una caída de SFleet no debe deshacer la póliza que ya quedó guardada en
/// dbLumoSys (ver el catch de <c>SincronizarSFleet</c>). Ese contrato se conserva, pero cada salida
/// por fallo reporta antes a Sentry el punto y el motivo: sin eso, SFleet caído se veía en el log
/// igual que "esta serie no está dada de alta en SFleet", que es un aviso rutinario y esperable.
/// </summary>
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

        try
        {
            var req = new RestRequest("authentications", Method.Post);
            req.AddParameter("email", _opts.Email);
            req.AddParameter("password", _opts.Password);

            var resp = await _http.ExecuteAsync(req, ct);
            if (!resp.IsSuccessStatusCode || resp.Content is null)
            {
                // Antes se devolvía null sin dejar registro: con las credenciales caídas, cada
                // póliza salía como "serie no encontrada en SFleet" (un aviso normal) y nada
                // indicaba que en realidad ninguna se estaba sincronizando.
                log.LogError("No se pudo autenticar contra SFleet: {Status}", resp.StatusCode);
                monitoreo.ReportarFalloSilencioso("sfleet.token",
                    $"SFleet rechazó la autenticación con {(int)resp.StatusCode} ({resp.StatusCode})",
                    "ninguna-poliza-se-sincroniza-con-sfleet",
                    ("status", ((int)resp.StatusCode).ToString()), ("email", _opts.Email));
                return null;
            }

            _token = JObject.Parse(resp.Content)["access_token"]?.ToString();

            if (string.IsNullOrWhiteSpace(_token))
            {
                monitoreo.ReportarFalloSilencioso("sfleet.token",
                    "SFleet respondió correctamente pero sin access_token",
                    "ninguna-poliza-se-sincroniza-con-sfleet",
                    ("email", _opts.Email));
                return null;
            }

            return _token;
        }
        catch (Exception ex)
        {
            // Cubre el JObject.Parse: si SFleet devuelve HTML de error con un 200, el parseo
            // truena y sin este catch la excepción se reportaría desde SincronizarSFleet, sin
            // decir que el problema fue la autenticación.
            monitoreo.Capturar(ex, "sfleet.token",
                ("consecuencia", "ninguna-poliza-se-sincroniza-con-sfleet"));
            log.LogError(ex, "Error autenticando contra SFleet");
            return null;
        }
    }

    public async Task<int?> BuscarVehiculo(string serie, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return null; // ObtenerToken ya reportó la causa raíz

        try
        {
            var req = new RestRequest("client_cars");
            req.AddHeader("Authorization", $"Bearer {token}");
            req.AddQueryParameter("q", serie);

            var resp = await _http.ExecuteGetAsync(req, ct);
            if (!resp.IsSuccessStatusCode || resp.Content is null)
            {
                // Sin esto, un 500 de SFleet se leía arriba como "Serie no encontrada en SFleet":
                // el mismo mensaje que cuando el vehículo legítimamente no está dado de alta.
                monitoreo.ReportarFalloSilencioso("sfleet.buscar-vehiculo",
                    $"SFleet respondió {(int)resp.StatusCode} ({resp.StatusCode})",
                    "poliza-no-sincronizada-se-lee-como-serie-inexistente",
                    ("serie", serie), ("status", ((int)resp.StatusCode).ToString()));
                return null;
            }

            return JArray.Parse(resp.Content).FirstOrDefault()?["id"]?.ToObject<int>();
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.buscar-vehiculo",
                ("serie", serie), ("consecuencia", "poliza-no-sincronizada"));
            log.LogError(ex, "Error consultando el vehículo {Serie} en SFleet", serie);
            return null;
        }
    }

    public async Task<int?> BuscarPoliza(string numeroPoliza, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return null;

        try
        {
            var req = new RestRequest("request_insurances");
            req.AddHeader("Authorization", $"Bearer {token}");
            req.AddQueryParameter("q", numeroPoliza);
            req.AddQueryParameter("per_page", "500");

            var resp = await _http.ExecuteGetAsync(req, ct);
            if (!resp.IsSuccessStatusCode || resp.Content is null)
            {
                // Consecuencia concreta: el llamador decide alta vs edición según este resultado,
                // así que un fallo aquí puede convertir una actualización en un alta duplicada.
                monitoreo.ReportarFalloSilencioso("sfleet.buscar-poliza",
                    $"SFleet respondió {(int)resp.StatusCode} ({resp.StatusCode})",
                    "se-tratara-como-alta-nueva-riesgo-de-duplicado",
                    ("poliza", numeroPoliza), ("status", ((int)resp.StatusCode).ToString()));
                return null;
            }

            return JArray.Parse(resp.Content).FirstOrDefault()?["id"]?.ToObject<int>();
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.buscar-poliza",
                ("poliza", numeroPoliza), ("consecuencia", "se-tratara-como-alta-nueva-riesgo-de-duplicado"));
            log.LogError(ex, "Error consultando la póliza {Poliza} en SFleet", numeroPoliza);
            return null;
        }
    }

    public async Task<int> GuardarPoliza(
        SolicitudSFleet solicitud, bool esEdicion, int vehiculoId, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return 0;

        try
        {
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

                // El retorno 0 no lo revisa nadie aguas arriba: sin este reporte, la póliza queda
                // guardada en dbLumoSys y ausente en SFleet sin dejar rastro de la divergencia.
                monitoreo.ReportarFalloSilencioso("sfleet.guardar-poliza",
                    $"SFleet rechazó la póliza con {(int)resp.StatusCode} ({resp.StatusCode})",
                    "guardada-en-lumosys-sin-sincronizar-sfleet",
                    ("poliza", solicitud.NumeroPoliza),
                    ("operacion_sfleet", esEdicion ? "edicion" : "alta"),
                    ("vehiculo_id", vehiculoId.ToString()),
                    ("status", ((int)resp.StatusCode).ToString()));
                return 0;
            }

            return JObject.Parse(resp.Content)["id"]?.ToObject<int>() ?? 0;
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.guardar-poliza",
                ("poliza", solicitud.NumeroPoliza),
                ("operacion_sfleet", esEdicion ? "edicion" : "alta"),
                ("consecuencia", "guardada-en-lumosys-sin-sincronizar-sfleet"));
            log.LogError(ex, "Error guardando la póliza {Poliza} en SFleet", solicitud.NumeroPoliza);
            return 0;
        }
    }

    public async Task<bool> SubirDocumento(
        int polizaId, int vehiculoId, string nombreArchivo, byte[] bytes, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return false;

        try
        {
            var req = new RestRequest($"client_cars/{vehiculoId}/files", Method.Post);
            req.AddHeader("Authorization", $"Bearer {token}");
            req.AddParameter("client_car_file[name]", nombreArchivo);
            req.AddParameter("client_car_file[serviable_type]", "RequestInsurance");
            req.AddParameter("client_car_file[serviable_id]", polizaId);
            req.AddParameter("client_car_file[documentation_car_id]", 2);
            req.AddFile("client_car_file[file]", bytes, nombreArchivo);

            var resp = await _http.ExecuteAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Este método era completamente mudo: devolvía false sin log ni evento.
                monitoreo.ReportarFalloSilencioso("sfleet.subir-documento",
                    $"SFleet respondió {(int)resp.StatusCode} ({resp.StatusCode})",
                    "documento-no-disponible-en-sfleet",
                    ("archivo", nombreArchivo),
                    ("poliza_sfleet_id", polizaId.ToString()),
                    ("vehiculo_id", vehiculoId.ToString()),
                    ("status", ((int)resp.StatusCode).ToString()));
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.subir-documento",
                ("archivo", nombreArchivo),
                ("vehiculo_id", vehiculoId.ToString()),
                ("consecuencia", "documento-no-disponible-en-sfleet"));
            log.LogError(ex, "Error subiendo el documento {Archivo} a SFleet", nombreArchivo);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
