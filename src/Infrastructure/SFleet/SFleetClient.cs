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
    IDiagnosticoRed diagnosticoRed,
    ILogger<SFleetClient> log) : ISFleetClient, IDisposable
{
    /// <summary>Para no repetir el mismo reporte de acceso en cada póliza de la corrida.</summary>
    private bool _accesoYaReportado;

    /// <summary>
    /// Reporta, una sola vez por corrida, que SFleet no deja operar — con los datos de conexión
    /// reales de este servidor.
    ///
    /// El caso concreto medido el 13/10/2026: <c>POST /authentications</c> responde 200 y entrega
    /// un JWT, pero TODOS los endpoints (<c>client_cars</c>, <c>request_insurances</c>,
    /// <c>insurers</c>) responden 401, con Bearer y sin él. Es decir, autentica pero no autoriza.
    /// Como <c>BuscarVehiculo</c> es la primera llamada, la sincronización se corta ahí y ninguna
    /// póliza ni documento llega a Fleet.
    /// </summary>
    private async Task ReportarAccesoDenegado(string operacion, int status, string? detalle, CancellationToken ct)
    {
        if (_accesoYaReportado) return;
        _accesoYaReportado = true;

        var red = await diagnosticoRed.SondearUrl("Fleet / SFleet · API", _opts.BaseUrl, ct);

        bool esAutorizacion = status is 401 or 403;

        monitoreo.ReportarFalloCritico("sfleet.acceso",
            $"SFleet respondió {status} en {operacion}: el servicio no autoriza las llamadas",
            "ninguna-poliza-ni-documento-llega-a-fleet",
            ("sfleet_url", _opts.BaseUrl),
            ("sfleet_email", _opts.Email),
            ("sfleet_operacion", operacion),
            ("sfleet_status", status.ToString()),
            ("sfleet_detalle", detalle),
            ("red_host", red.Host),
            ("red_puerto", red.Puerto.ToString()),
            ("red_dns", red.DnsResuelve ? string.Join(", ", red.Ips) : "NO RESUELVE"),
            ("red_tcp", red.TcpConecta switch { true => $"OK ({red.MilisegundosTcp} ms)", false => "BLOQUEADO", _ => "no probado" }),
            ("red_resumen", red.Resumen),
            ("que_pedir", esAutorizacion && red.Alcanzable
                ? $"La red hacia {red.Host}:{red.Puerto} está bien y la autenticación devuelve token, pero las " +
                  $"llamadas responden {status}. NO es un problema de red ni de contraseña: PEDIR AL PROVEEDOR DE FLEET " +
                  $"que habilite los permisos de API de la cuenta '{_opts.Email}' sobre client_cars, request_insurances " +
                  "e insurers (lectura y escritura)."
                : red.QuePedir));

        log.LogError("SFleet: {Operacion} respondió {Status}. {Resumen}", operacion, status, red.Resumen);
    }
    private readonly RestClient _http = new(opts.Value.BaseUrl);
    private readonly SFleetOptions _opts = opts.Value;
    private string? _token;

    /// <summary>Catálogo de aseguradoras de SFleet, cacheado por nombre en mayúsculas. El cliente es
    /// scoped (una instancia por lote), así que esto evita repetir GET /insurers por cada póliza
    /// sin arriesgarse a servir un catálogo rancio entre corridas.</summary>
    private readonly Dictionary<string, int> _aseguradoras = new(StringComparer.OrdinalIgnoreCase);

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
                // Warning y no Error: el ReportarFalloCritico de abajo ya emite el evento con su
                // consecuencia; un LogError aqui generaria un segundo evento del mismo fallo.
                log.LogWarning("No se pudo autenticar contra SFleet: {Status}", resp.StatusCode);
                // Critico y no informativo: sin token no se sincroniza NINGUNA poliza, no es el
                // fallo de un registro. Es el caso que estuvo semanas sin detectarse porque cada
                // poliza salia como "serie no encontrada", que es un aviso rutinario.
                monitoreo.ReportarFalloCritico("sfleet.token",
                    $"SFleet rechazó la autenticación con {(int)resp.StatusCode} ({resp.StatusCode})",
                    "ninguna-poliza-se-sincroniza-con-sfleet",
                    ("status", ((int)resp.StatusCode).ToString()), ("email", _opts.Email));
                return null;
            }

            _token = JObject.Parse(resp.Content)["access_token"]?.ToString();

            if (string.IsNullOrWhiteSpace(_token))
            {
                monitoreo.ReportarFalloCritico("sfleet.token",
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

                // Primera llamada de la sincronización: si aquí no se puede operar, nada llega a
                // Fleet. Se emite el reporte con los datos de conexión reales, una vez por corrida.
                await ReportarAccesoDenegado("client_cars", (int)resp.StatusCode, resp.Content, ct);
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

            var resultados = JArray.Parse(resp.Content);
            if (resultados.Count == 0)
                return null;

            // Guarda del PATCH: `q=` es una búsqueda difusa y el primer resultado no tiene por qué
            // ser esta póliza. Devolver ese id convertiría el PATCH de GuardarPoliza en una
            // sobrescritura de la póliza de otro cliente. Solo se acepta la coincidencia exacta.
            var exacta = resultados.FirstOrDefault(x =>
                string.Equals(
                    x["police"]?.ToString()?.Trim(),
                    numeroPoliza.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            if (exacta is null)
            {
                monitoreo.ReportarFalloSilencioso("sfleet.buscar-poliza",
                    $"SFleet devolvió {resultados.Count} resultado(s) pero ninguno con police exactamente igual",
                    "se-dara-de-alta-en-vez-de-actualizar-para-no-sobrescribir-una-poliza-ajena",
                    ("poliza", numeroPoliza),
                    ("resultados", resultados.Count.ToString()),
                    ("primer_police", resultados[0]["police"]?.ToString()));
                return null;
            }

            return exacta["id"]?.ToObject<int>();
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.buscar-poliza",
                ("poliza", numeroPoliza), ("consecuencia", "se-tratara-como-alta-nueva-riesgo-de-duplicado"));
            log.LogError(ex, "Error consultando la póliza {Poliza} en SFleet", numeroPoliza);
            return null;
        }
    }

    /// <summary>
    /// Alta o actualización de la póliza en SFleet.
    ///
    /// La ruta de edición lleva el id de la PÓLIZA en SFleet. Antes se le pasaba el id del
    /// VEHÍCULO, de modo que un PATCH habría caído sobre la póliza cuyo id coincidiera con ese
    /// número: una póliza de otro cliente del proveedor. No llegó a ocurrir solo porque SFleet
    /// rechaza hoy todas las peticiones con 422 por campos faltantes; al corregir el payload, el
    /// error se habría vuelto destructivo.
    /// </summary>
    public async Task<int> GuardarPoliza(
        SolicitudSFleet solicitud, int? polizaSFleetId, CancellationToken ct = default)
    {
        string? token = await ObtenerToken(ct);
        if (token is null) return 0;

        // Fuera del try porque el catch también lo reporta.
        bool esEdicion = polizaSFleetId.HasValue;

        try
        {
            var metodo = esEdicion ? Method.Patch : Method.Post;
            var ruta   = esEdicion ? $"request_insurances/{polizaSFleetId!.Value}" : "request_insurances";

            int insurerId = await ResolverAseguradoraId(solicitud.Aseguradora, token, ct);

            var req = new RestRequest(ruta, metodo);
            req.AddHeader("Authorization", $"Bearer {token}");

            // El cuerpo reproduce el contrato completo del ETL legacy. Mandar solo 8 de los ~40
            // campos hacía que SFleet contestara 422 a TODA póliza: ninguna se daba de alta y,
            // por tanto, ningún documento tenía a qué adjuntarse. Los campos que SICAS no provee
            // van en 0/vacío igual que en el legacy, para no alterar la forma del payload.
            req.AddJsonBody(new
            {
                request_insurance = new
                {
                    police                = solicitud.NumeroPoliza,
                    portfolio_management  = solicitud.AdministracionCartera,
                    net_premium           = solicitud.PrimaNeta,
                    coverage              = solicitud.Cobertura,
                    incident_executive    = solicitud.Ejecutivo,
                    date_start            = solicitud.FechaInicio.ToString("yyyy-MM-dd"),
                    date_end              = solicitud.FechaVencimiento.ToString("yyyy-MM-dd"),
                    subsection            = (solicitud.Inciso ?? 1).ToString(),
                    payment_management    = solicitud.GestionPago,
                    beneficiary           = solicitud.Beneficiario,
                    amount                = solicitud.PrimaTotal,
                    executive             = solicitud.Ejecutivo,
                    contract_number       = 0,
                    contractor            = string.Empty,
                    asset_description     = solicitud.Adaptacion,
                    adaptation_description = solicitud.Adaptacion,
                    invoice_value         = solicitud.ValorFactura ?? 0,
                    value_adaptation      = solicitud.ValorAdaptacion ?? 0,
                    passengers            = solicitud.Pasajeros,
                    broker                = solicitud.Broker,
                    type_policy           = solicitud.TipoPoliza,
                    issuance_date         = DateTime.Now.ToString("yyyy-MM-dd"),
                    deductible_payment    = 0,
                    days                  = solicitud.Dias,
                    preferent_beneficiary = solicitud.BeneficiarioPreferente,
                    use                   = solicitud.TipoUso,
                    way_to_pay            = solicitud.FormaPago,
                    udi                   = 0,
                    surcharges            = solicitud.Recargos ?? 0,
                    rights                = solicitud.Derechos ?? 0,
                    iva                   = solicitud.IVA ?? 0,
                    total                 = solicitud.PrimaTotal,
                    first_receipt         = (solicitud.PrimerRecibo ?? 0).ToString(),
                    subsequent            = (solicitud.Subsecuente ?? 0).ToString(),
                    total_receipts        = 0,
                    amount_udis           = 0,
                    rc_aditionals         = solicitud.ResponsabilidadCivilAdicionales,
                    comments              = string.Empty,
                    catalogue_id          = 15,
                    insurer_id            = insurerId,
                    client_car_id         = solicitud.ClienteVehiculoId,
                    client_car_file_id    = 1
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
                    ("poliza_sfleet_id", polizaSFleetId?.ToString()),
                    ("vehiculo_id", solicitud.ClienteVehiculoId.ToString()),
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

    /// <summary>
    /// Traduce el nombre de la aseguradora de SICAS al id que usa SFleet (GET /insurers), igual
    /// que hacía <c>ObtenerAseguradoraSFleet</c> en el ETL legacy. Devuelve 0 si no se puede
    /// resolver, que es lo que el legacy mandaba también; eso no se trata como error porque el
    /// alta puede seguir siendo válida, pero sí se reporta para poder detectar un catálogo
    /// desalineado entre los dos sistemas.
    /// </summary>
    private async Task<int> ResolverAseguradoraId(string? aseguradora, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(aseguradora))
            return 0;

        string clave = aseguradora.Trim().ToUpperInvariant();
        if (_aseguradoras.TryGetValue(clave, out int cacheado))
            return cacheado;

        try
        {
            var req = new RestRequest("insurers");
            req.AddHeader("Authorization", $"Bearer {token}");

            var resp = await _http.ExecuteGetAsync(req, ct);
            if (!resp.IsSuccessStatusCode || resp.Content is null)
            {
                monitoreo.ReportarFalloSilencioso("sfleet.buscar-aseguradora",
                    $"SFleet respondió {(int)resp.StatusCode} ({resp.StatusCode}) al listar aseguradoras",
                    "poliza-se-envia-con-insurer_id-0",
                    ("aseguradora", aseguradora), ("status", ((int)resp.StatusCode).ToString()));
                return 0;
            }

            int id = JArray.Parse(resp.Content)
                .FirstOrDefault(x => string.Equals(
                    x["name"]?.ToString()?.Trim(), clave, StringComparison.OrdinalIgnoreCase))
                ?["id"]?.ToObject<int>() ?? 0;

            if (id == 0)
            {
                monitoreo.ReportarFalloSilencioso("sfleet.buscar-aseguradora",
                    $"La aseguradora '{aseguradora}' no está en el catálogo de SFleet",
                    "poliza-se-envia-con-insurer_id-0",
                    ("aseguradora", aseguradora));
            }

            _aseguradoras[clave] = id;
            return id;
        }
        catch (Exception ex)
        {
            monitoreo.Capturar(ex, "sfleet.buscar-aseguradora",
                ("aseguradora", aseguradora), ("consecuencia", "poliza-se-envia-con-insurer_id-0"));
            log.LogError(ex, "Error consultando el catálogo de aseguradoras de SFleet");
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
