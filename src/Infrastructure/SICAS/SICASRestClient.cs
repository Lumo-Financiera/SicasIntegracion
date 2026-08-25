using System.Linq;
using System.Text;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace LumoSys.Integraciones.Infrastructure.SICAS;

public sealed class SICASOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Contrasena { get; init; } = string.Empty;
}

/// <summary>
/// Cliente singleton que maneja el ciclo de vida del token SICAS REST (TTL 3 min).
/// Todos los métodos de negocio (SICASSeguroClient, SICASSiniestroClient) delegan aquí.
/// </summary>
public sealed class SICASRestClient : ISICASRestClient, IDisposable
{
    private readonly RestClient _http;
    private readonly HttpClient _authHttp = new();
    private readonly string _baseUrl;
    private readonly string _usuario;
    private readonly string _contrasena;
    private readonly ILogger<SICASRestClient> _log;

    private string? _token;
    private DateTime _tokenExpira = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public SICASRestClient(IOptions<SICASOptions> opts, ILogger<SICASRestClient> log)
    {
        _baseUrl    = opts.Value.BaseUrl.TrimEnd('/');
        _usuario    = opts.Value.Usuario;
        _contrasena = opts.Value.Contrasena;
        _log        = log;
        _http       = new RestClient(opts.Value.BaseUrl);
    }

    // ─── Token ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Autenticación básica de SICAS (POST /Security/GetToken con sUserName/sPassword por
    /// query string). El endpoint exige Content-Length aunque el body vaya vacío (HTTP 411
    /// si se omite), por eso se usa HttpClient con un StringContent vacío en vez de RestSharp.
    /// </summary>
    private async Task<string?> EnsureToken(CancellationToken ct)
    {
        if (_token is not null && DateTime.Now < _tokenExpira)
            return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.Now < _tokenExpira)
                return _token;

            string url = $"{_baseUrl}/Security/GetToken" +
                $"?sUserName={Uri.EscapeDataString(_usuario)}&sPassword={Uri.EscapeDataString(_contrasena)}";

            HttpResponseMessage resp;
            try
            {
                resp = await _authHttp.PostAsync(url, new StringContent(string.Empty), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin este catch la excepción de red subía cruda hasta el middleware, y el log
                // mostraba un stack trace de sockets en vez de decir que SICAS no responde.
                _log.LogError(ex, "No se pudo contactar a SICAS en {Url} para obtener el token. " +
                                  "Ningun dato se va a sincronizar hasta que responda.", _baseUrl);
                return null;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogError("No se pudo obtener token SICAS: HTTP {Status} ({Codigo}).",
                        resp.StatusCode, (int)resp.StatusCode);
                    return null;
                }

                string contenido = await resp.Content.ReadAsStringAsync(ct);
                var json = JObject.Parse(contenido);

                if (json["Sucess"]?.ToObject<bool>() != true)
                {
                    _log.LogError("SICAS rechazó la autenticación del usuario configurado: {Mensaje}",
                        json["Message"]?.ToString());
                    return null;
                }

                bool esRenovacion = _token is not null;
                _token      = json["Token"]?.ToString();
                _tokenExpira = DateTime.Now.AddSeconds(150); // 2.5 min (TTL 3 min)

                // Information y no Debug: es el latido que confirma que la conexión con SICAS vive.
                // Si el log deja de mostrar esto durante un lote, el ETL está atorado antes de SICAS.
                _log.LogInformation("Token SICAS {Accion}, expira {Expira:HH:mm:ss}",
                    esRenovacion ? "renovado" : "obtenido", _tokenExpira);
                return _token;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ─── ReadData<T> ──────────────────────────────────────────────────────────

    /// <summary>
    /// Llama a /Report/ReadData según el contrato real de SICAS: el KeyCode va en el
    /// header "Prop_KeyCode" (no en el body), el token va sin prefijo "Bearer", y el body
    /// son parámetros de formulario (PageRequested/ItemsForPages/SortFields/Conditions),
    /// no JSON. La respuesta tiene forma {"Response":[{"&lt;NombreTabla&gt;":{"Data":[...]}}]}
    /// — el nombre de la tabla varía según el KeyCode, por eso se toma la primera propiedad.
    /// </summary>
    public async Task<List<T>?> ReadData<T>(SolicitudReadData solicitud, CancellationToken ct = default) where T : class
    {
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        string? token = await EnsureToken(ct);
        if (token is null)
        {
            _log.LogError("ReadData {KeyCode}: sin token, no se pudo consultar SICAS.", solicitud.KeyCode);
            return null;
        }

        var req = new RestRequest("Report/ReadData", Method.Post);
        req.AddHeader("Authorization", token);
        req.AddHeader("Prop_KeyCode", solicitud.KeyCode);
        req.AddParameter("PageRequested", solicitud.Page);
        req.AddParameter("ItemsForPages", solicitud.ItemForPage);
        req.AddParameter("FormatResponse", 2); // JSON
        if (!string.IsNullOrEmpty(solicitud.InfoSort))
            req.AddParameter("SortFields", solicitud.InfoSort);
        if (solicitud.Conditions.Count > 0)
            req.AddParameter("Conditions", ConstruirConditions(solicitud.Conditions));

        var resp = await EjecutarConReintentos(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null)
        {
            // Error, no Warning: un ReadData fallido significa que ese pedazo de datos no se
            // procesó. Antes esto se confundía con "no hay registros" al leer el log.
            _log.LogError("ReadData {KeyCode} pagina {Pagina} FALLO: {Status} tras {Ms} ms. " +
                          "Los registros de esta consulta NO se procesaron.",
                solicitud.KeyCode, solicitud.Page, resp.StatusCode, cronometro.ElapsedMilliseconds);
            return null;
        }

        string contenido = resp.Content;
        const int maxReintentos = 5;

        for (int intento = 0; intento < maxReintentos; intento++)
        {
            try
            {
                var filas = ExtraerFilas<T>(contenido);

                // Distingue explícitamente "0 registros" de "falló la consulta": son dos cosas
                // muy distintas al diagnosticar y antes se veían igual en el log.
                _log.LogDebug("ReadData {KeyCode} pagina {Pagina}: {Count} registros en {Ms} ms",
                    solicitud.KeyCode, solicitud.Page, filas?.Count ?? 0, cronometro.ElapsedMilliseconds);

                // Una consulta lenta explica por sí sola un lote que no termina.
                if (cronometro.ElapsedMilliseconds > 15_000)
                    _log.LogWarning("ReadData {KeyCode} pagina {Pagina} tardo {Ms} ms ({Count} registros).",
                        solicitud.KeyCode, solicitud.Page, cronometro.ElapsedMilliseconds, filas?.Count ?? 0);

                return filas;
            }
            catch (JsonReaderException ex)
            {
                // SICAS puede devolver JSON con caracteres inválidos — mismo patrón que el SOAP actual
                if (ex.LinePosition > 0 && ex.LinePosition <= contenido.Length)
                {
                    contenido = contenido.Remove((int)ex.LinePosition - 1, 1);
                    _log.LogDebug("JSON reparado en posición {Pos}, reintento {N}", ex.LinePosition, intento + 1);
                }
                else
                {
                    _log.LogError(ex, "JSON invalido e irreparable en ReadData {KeyCode} pagina {Pagina}; " +
                                      "los registros de esta consulta NO se procesaron.",
                        solicitud.KeyCode, solicitud.Page);
                    return null;
                }
            }
        }

        _log.LogError("ReadData {KeyCode} pagina {Pagina}: JSON no se pudo reparar tras {Max} intentos; " +
                      "los registros de esta consulta NO se procesaron.",
            solicitud.KeyCode, solicitud.Page, maxReintentos);
        return null;
    }

    /// <summary>
    /// SICAS a veces corta la conexión (StatusCode 0) cuando recibe ráfagas de llamadas
    /// seguidas — se recuperó solo tras unos segundos al probarlo manualmente. Reintenta
    /// solo fallos de conexión (no reintenta 4xx/5xx reales de la aplicación).
    /// </summary>
    private async Task<RestResponse> EjecutarConReintentos(RestRequest req, CancellationToken ct)
    {
        const int maxIntentos = 3;
        RestResponse resp;
        int intento = 1;
        while (true)
        {
            resp = await _http.ExecuteAsync(req, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode != 0 || intento >= maxIntentos)
            {
                if (intento > 1 && resp.IsSuccessStatusCode)
                    _log.LogInformation("SICAS respondio tras {Intentos} intentos ({Recurso}).",
                        intento, req.Resource);
                else if (!resp.IsSuccessStatusCode && intento >= maxIntentos)
                    _log.LogError("SICAS no respondio tras {Max} intentos ({Recurso}); se abandona la llamada.",
                        maxIntentos, req.Resource);
                return resp;
            }

            // Warning y no Debug: una caída de conexión con SICAS es señal operativa real,
            // y con el nivel Debug apagado en producción no quedaba constancia de ninguna.
            _log.LogWarning("Fallo de conexion con SICAS en {Recurso} (intento {Intento}/{Max}), " +
                            "reintentando en {Segundos}s",
                req.Resource, intento, maxIntentos, intento * 2);
            await Task.Delay(TimeSpan.FromSeconds(intento * 2), ct);
            intento++;
        }
    }

    private static List<T>? ExtraerFilas<T>(string json) where T : class
    {
        var root = JObject.Parse(json);
        var response = root["Response"] as JArray;
        if (response is null || response.Count == 0)
            return [];

        var tabla = (response[0] as JObject)?.Properties().FirstOrDefault();
        var datos = tabla?.Value["Data"] as JArray;
        return datos?.ToObject<List<T>>() ?? [];
    }

    private static string ConstruirConditions(List<CondicionSICAS> condiciones) =>
        string.Join("!", condiciones.Select(c =>
            $"{c.Label};{c.FilterType};{c.SubFilter};{c.Values};{c.Texts};{c.PosTitle};{c.ChangeTable};{c.ColumnName}"));

    // ─── DigitalCenter ────────────────────────────────────────────────────────

    public async Task<List<ArchivoSICAS>> BuscarArchivosDigitales(string identity, long valuePK, CancellationToken ct = default)
    {
        string? token = await EnsureToken(ct);
        if (token is null) return [];

        // /DigitalCenter/GetFiles con TypeReadBasic=true: lectura general del Centro Digital,
        // sin depender de la configuración especial por agente/corredor que usa GetFilesAdv
        // (esa truena con "Internal error server" para esta licencia — confirmado contra
        // el manual SICAS y verificado en vivo: GetFiles sí devuelve los archivos reales).
        var req = new RestRequest("DigitalCenter/GetFiles", Method.Post);
        req.AddHeader("Authorization", token);
        req.AddJsonBody(new
        {
            FolderRead = 1, // Storage (Centro Digital)
            Identity = identity,
            ValuePK = valuePK,
            TypeReadBasic = true,
            ReadRecursive = false,
            URLSecurity = 0 // URL directa, requerido por DownloadFile
        });

        var resp = await EjecutarConReintentos(req, ct);
        if (!resp.IsSuccessStatusCode || resp.Content is null)
        {
            _log.LogWarning("GetFiles identity={Identity} valuePK={PK} falló: {Status}",
                identity, valuePK, resp.StatusCode);
            return [];
        }

        try
        {
            var resultado = JObject.Parse(resp.Content);

            if (resultado["Sucess"]?.ToObject<bool>() == false)
            {
                _log.LogDebug("GetFiles identity={Identity} valuePK={PK}: {Mensaje}",
                    identity, valuePK, resultado["Error"]?.ToString());
                return [];
            }

            var datos = resultado["ListData"]?.ToObject<List<ArchivoSICAS>>();
            var conRuta = datos?.Where(a => !string.IsNullOrEmpty(a.PathWWW)).ToList() ?? [];

            // Deja constancia de los que SICAS listó pero vienen sin PathWWW: no se pueden bajar
            // y antes desaparecían del conteo sin explicación.
            int descartados = (datos?.Count ?? 0) - conRuta.Count;
            if (descartados > 0)
                _log.LogWarning("GetFiles identity={Identity} valuePK={PK}: {Descartados} archivo(s) sin PathWWW; " +
                                "no se pueden descargar.", identity, valuePK, descartados);

            _log.LogDebug("GetFiles identity={Identity} valuePK={PK}: {Count} archivo(s) descargables.",
                identity, valuePK, conRuta.Count);

            return conRuta;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error parseando GetFiles identity={Identity} valuePK={PK}; " +
                              "los documentos de esta entidad NO se procesaron.", identity, valuePK);
            return [];
        }
    }

    public async Task<byte[]?> DownloadFile(string fileUrl, CancellationToken ct = default)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var resp = await httpClient.GetAsync(fileUrl, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("DownloadFile {Url} → {Status}", fileUrl, resp.StatusCode);
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error descargando archivo {Url}", fileUrl);
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
