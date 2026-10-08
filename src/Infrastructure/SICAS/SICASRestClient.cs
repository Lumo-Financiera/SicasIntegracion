using System.Globalization;
using System.Linq;
using System.Net;
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
    private readonly IMonitoreoErrores _monitoreo;

    private string? _token;
    private DateTime _tokenExpira = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public SICASRestClient(
        IOptions<SICASOptions> opts, IMonitoreoErrores monitoreo, ILogger<SICASRestClient> log)
    {
        _baseUrl    = opts.Value.BaseUrl.TrimEnd('/');
        _usuario    = opts.Value.Usuario;
        _contrasena = opts.Value.Contrasena;
        _log        = log;
        _monitoreo  = monitoreo;
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

            using var resp = await _authHttp.PostAsync(url, new StringContent(string.Empty), ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("No se pudo obtener token SICAS: {Status}", resp.StatusCode);
                _monitoreo.RastrearFallo("sicas.token", "GetToken no respondió correctamente",
                    ("status", resp.StatusCode.ToString()), ("usuario", _usuario));
                return null;
            }

            string contenido = await resp.Content.ReadAsStringAsync(ct);
            var json = JObject.Parse(contenido);

            if (json["Sucess"]?.ToObject<bool>() != true)
            {
                _log.LogError("SICAS rechazó la autenticación: {Mensaje}", json["Message"]?.ToString());
                _monitoreo.RastrearFallo("sicas.token", "SICAS rechazó la autenticación",
                    ("mensaje", json["Message"]?.ToString()), ("usuario", _usuario));
                return null;
            }

            _token      = json["Token"]?.ToString();
            _tokenExpira = DateTime.Now.AddSeconds(150); // 2.5 min (TTL 3 min)

            _log.LogDebug("Token SICAS obtenido, expira: {Expira:HH:mm:ss}", _tokenExpira);
            return _token;
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
        string? token = await EnsureToken(ct);
        if (token is null)
        {
            // Rastro y no evento a propósito: EnsureToken ya reportó la causa raíz con LogError.
            // Emitir aquí otro evento por cada consulta multiplicaría un solo fallo en decenas.
            _monitoreo.RastrearFallo("sicas.readdata", $"Sin token: no se consultó {solicitud.KeyCode}",
                ("keycode", solicitud.KeyCode), ("pagina", solicitud.Page.ToString()));
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
            _log.LogWarning("ReadData {KeyCode} falló: {Status}", solicitud.KeyCode, resp.StatusCode);

            // Éste es el fallo silencioso más peligroso del integrador: los clientes de dominio
            // convierten este null en lista vacía (`resp ?? []`), el barrido lo lee como "no hay
            // registros", corta el recorrido de páginas y se declara exitoso. Sin este reporte,
            // "SICAS está caído" y "hoy no hubo pólizas" son indistinguibles.
            _monitoreo.ReportarFalloSilencioso("sicas.readdata",
                $"SICAS respondió {(int)resp.StatusCode} ({resp.StatusCode}) al consultar {solicitud.KeyCode}",
                "consulta-descartada-se-lee-como-sin-registros",
                ("keycode", solicitud.KeyCode),
                ("status", ((int)resp.StatusCode).ToString()),
                ("pagina", solicitud.Page.ToString()));
            return null;
        }

        string contenido = resp.Content;
        const int maxReintentos = 5;

        for (int intento = 0; intento < maxReintentos; intento++)
        {
            try
            {
                return ExtraerFilas<T>(contenido);
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
                    _log.LogWarning(ex, "JSON inválido en ReadData {KeyCode}", solicitud.KeyCode);
                    // El JSON malformado de SICAS se repara hasta 5 veces; llegar aquí significa
                    // que la reparación no alcanzó y ese registro se pierde en silencio.
                    _monitoreo.Capturar(ex, "sicas.readdata",
                        ("keycode", solicitud.KeyCode),
                        ("pagina", solicitud.Page.ToString()),
                        ("consecuencia", "respuesta-descartada-json-irreparable"));
                    return null;
                }
            }
        }

        // Se agotaron los 5 intentos de reparación sin conseguir un JSON válido. Mismo efecto que
        // arriba: el llamador lo verá como "sin registros".
        _monitoreo.ReportarFalloSilencioso("sicas.readdata",
            $"JSON de {solicitud.KeyCode} sigue siendo inválido tras {maxReintentos} reparaciones",
            "consulta-descartada-se-lee-como-sin-registros",
            ("keycode", solicitud.KeyCode),
            ("pagina", solicitud.Page.ToString()));
        return null;
    }

    /// <summary>
    /// SICAS a veces corta la conexión (StatusCode 0) cuando recibe ráfagas de llamadas
    /// seguidas — se recuperó solo tras unos segundos al probarlo manualmente. Reintenta
    /// solo fallos de conexión (no reintenta 4xx/5xx reales de la aplicación).
    /// </summary>
    private async Task<RestResponse> EjecutarConReintentos(RestRequest req, CancellationToken ct)
    {
        const int maxIntentos = 5;
        RestResponse resp;
        int intento = 1;
        while (true)
        {
            try
            {
                resp = await _http.ExecuteAsync(req, ct);
            }
            catch (Exception ex) when (intento < maxIntentos
                                       && !ct.IsCancellationRequested
                                       && EsFalloDeRed(ex))
            {
                // La conexión se cortó ANTES de que hubiera una respuesta que inspeccionar, así que
                // este caso no pasa por EsTransitorio: aquí no hay RestResponse, hay excepción.
                // Ocurrió el 07/10/2026 con un SocketException 10054 ("conexión forzada por el host
                // remoto") en mitad de la reconciliación; sin este catch, ese siniestro se queda sin
                // revisar hasta el día siguiente aunque el corte durara un segundo.
                TimeSpan esperaRed = TimeSpan.FromSeconds(Math.Pow(2, intento));

                _log.LogWarning(ex, "Se cortó la conexión con SICAS (intento {Intento}/{Max}); se reintenta en {Segundos}s",
                    intento, maxIntentos, esperaRed.TotalSeconds);

                await Task.Delay(esperaRed, ct);
                intento++;
                continue;
            }

            if (resp.IsSuccessStatusCode || intento >= maxIntentos || !EsTransitorio(resp))
                return resp;

            TimeSpan espera = EsperaSugerida(resp) ?? TimeSpan.FromSeconds(Math.Pow(2, intento));

            _log.LogWarning("SICAS respondió {Status} (intento {Intento}/{Max}); se reintenta en {Segundos}s",
                resp.StatusCode == 0 ? "sin conexión" : resp.StatusCode.ToString(),
                intento, maxIntentos, espera.TotalSeconds);

            await Task.Delay(espera, ct);
            intento++;
        }
    }

    /// <summary>
    /// Distingue el fallo que vale la pena reintentar del que no.
    ///
    /// Antes solo se reintentaba con <c>StatusCode == 0</c>, es decir cuando ni siquiera hubo
    /// respuesta. Un 429 trae codigo valido, asi que salia al primer intento y la consulta se
    /// descartaba: aguas arriba eso se leia como "este siniestro no existe en SICAS" -un aviso
    /// rutinario- y el folio se quedaba sin sincronizar sin que nadie lo notara. Cuanto mas rapido
    /// procesa el barrido, mas folios se pierden asi.
    ///
    /// No se reintenta un 401/403/404: ahi la respuesta no va a cambiar por insistir.
    /// </summary>
    /// <summary>
    /// Caídas de red que merecen otro intento: la conexión se perdió, se agotó el tiempo o el TLS
    /// se cortó a media lectura. Son fallos del transporte, no del contenido de la petición, así
    /// que repetirla tiene sentido.
    ///
    /// Un <c>TaskCanceledException</c> aquí es el timeout del propio HttpClient; cuando viene del
    /// apagado del servicio no llega a este punto, porque quien llama ya descartó ese caso mirando
    /// el CancellationToken.
    /// </summary>
    private static bool EsFalloDeRed(Exception ex) =>
        ex is HttpRequestException
           or IOException
           or System.Net.Sockets.SocketException
           or TaskCanceledException
        || ex.InnerException is not null && EsFalloDeRed(ex.InnerException);

    private static bool EsTransitorio(RestResponse resp) =>
        resp.StatusCode == 0                                  // no hubo respuesta
        || resp.StatusCode == HttpStatusCode.TooManyRequests   // 429, el limite de tasa de SICAS
        || resp.StatusCode == HttpStatusCode.RequestTimeout    // 408
        || (int)resp.StatusCode is 502 or 503 or 504;          // caidas momentaneas del gateway

    /// <summary>
    /// Respeta el <c>Retry-After</c> cuando SICAS lo manda, en segundos o como fecha HTTP. Devuelve
    /// null si no viene o no es interpretable, para caer al respaldo exponencial. Se acota a 60 s:
    /// una espera mayor bloquearia el barrido mas de lo que conviene.
    /// </summary>
    private static TimeSpan? EsperaSugerida(RestResponse resp)
    {
        string? valor = resp.Headers?
            .FirstOrDefault(h => string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase))?
            .Value?.ToString();

        if (string.IsNullOrWhiteSpace(valor)) return null;

        if (int.TryParse(valor, NumberStyles.Integer, CultureInfo.InvariantCulture, out int segundos))
            return TimeSpan.FromSeconds(Math.Clamp(segundos, 1, 60));

        if (DateTimeOffset.TryParse(valor, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal, out var cuando))
        {
            double faltan = (cuando - DateTimeOffset.UtcNow).TotalSeconds;
            if (faltan > 0) return TimeSpan.FromSeconds(Math.Clamp(faltan, 1, 60));
        }

        return null;
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
        if (token is null)
        {
            // Rastro, no evento: la causa raíz ya la reportó EnsureToken (ver ReadData).
            _monitoreo.RastrearFallo("sicas.getfiles", "Sin token: no se listaron documentos",
                ("identity", identity), ("valuepk", valuePK.ToString()));
            return [];
        }

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

            // Lista vacía = "esta póliza/siniestro no tiene documentos". El registro se guarda
            // igual y nadie nota que sus documentos nunca se subieron.
            _monitoreo.ReportarFalloSilencioso("sicas.getfiles",
                $"SICAS respondió {(int)resp.StatusCode} ({resp.StatusCode}) al listar documentos",
                "documentos-no-listados-se-lee-como-sin-documentos",
                ("identity", identity),
                ("valuepk", valuePK.ToString()),
                ("status", ((int)resp.StatusCode).ToString()));
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
            return datos?.Where(a => !string.IsNullOrEmpty(a.PathWWW)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _monitoreo.Capturar(ex, "sicas.getfiles",
                ("identity", identity),
                ("valuepk", valuePK.ToString()),
                ("consecuencia", "documentos-no-listados"));

            _log.LogWarning(ex, "Error parseando GetFiles");
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

                // El llamador trata el null como "documento no disponible" y sigue con el
                // siguiente: la póliza queda guardada y sin ese documento, sin ninguna señal.
                _monitoreo.ReportarFalloSilencioso("sicas.descargar-archivo",
                    $"La descarga devolvió {(int)resp.StatusCode} ({resp.StatusCode})",
                    "documento-no-descargado",
                    ("archivo_url", fileUrl),
                    ("status", ((int)resp.StatusCode).ToString()));
                return null;
            }

            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            // Se conserva el retorno null (el llamador lo trata como "documento no disponible" y
            // sigue con el siguiente), pero se reporta: un documento que nunca llega es la falla
            // más difícil de notar, porque la póliza/siniestro sí queda guardado.
            _monitoreo.Capturar(ex, "sicas.descargar-archivo",
                ("archivo_url", fileUrl),
                ("consecuencia", "documento-no-descargado"));

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
