namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

/// <summary>
/// Resultado de sondear un destino de red desde el servidor donde corre el integrador.
///
/// Existe porque quien opera este servicio no tiene acceso al servidor: cuando algo no conecta,
/// necesita poder decirle a soporte exactamente QUÉ nombre, QUÉ IP y QUÉ puerto hay que habilitar,
/// sin suponer que la configuración local es la misma que la de producción.
/// </summary>
/// <param name="Destino">Nombre legible del destino: "SICAS · API", "FTP de documentos", etc.</param>
/// <param name="Url">La URL o endpoint tal como lo tiene configurado ESTE servidor.</param>
/// <param name="Host">Nombre del host extraído de la URL.</param>
/// <param name="Puerto">Puerto efectivo (el explícito, o el que implica el esquema).</param>
/// <param name="Ips">IPs a las que resolvió el nombre. Vacío si el DNS falló.</param>
/// <param name="DnsResuelve">false = el servidor no puede traducir el nombre a IP.</param>
/// <param name="TcpConecta">true = se logró abrir el socket al puerto. null = no se intentó (sin DNS).</param>
/// <param name="MilisegundosTcp">Cuánto tardó el handshake TCP, si conectó.</param>
/// <param name="Detalle">Mensaje del error cuando algo falló.</param>
public sealed record SondeoDestino(
    string Destino,
    string Url,
    string Host,
    int Puerto,
    string[] Ips,
    bool DnsResuelve,
    bool? TcpConecta,
    long? MilisegundosTcp,
    string? Detalle)
{
    /// <summary>true cuando el nombre resuelve y el puerto acepta conexión.</summary>
    public bool Alcanzable => DnsResuelve && TcpConecta == true;

    /// <summary>Una línea por destino, pensada para leerse en el evento de Sentry.</summary>
    public string Resumen =>
        $"{Destino} | {Host}:{Puerto} | DNS: {(DnsResuelve ? string.Join(", ", Ips) : "NO RESUELVE")} | " +
        $"TCP: {(TcpConecta switch { true => $"OK ({MilisegundosTcp} ms)", false => "BLOQUEADO", _ => "no probado" })}" +
        (string.IsNullOrWhiteSpace(Detalle) ? string.Empty : $" | {Detalle}");

    /// <summary>
    /// Qué hay que pedirle a soporte. Es el campo que se copia del evento y se pega en el ticket,
    /// por eso nombra el host y el puerto concretos en vez de hablar en general.
    /// </summary>
    public string QuePedir => this switch
    {
        { DnsResuelve: false } =>
            $"El servidor NO resuelve el nombre '{Host}'. PEDIR A SOPORTE: habilitar la resolución DNS de " +
            $"'{Host}' desde este servidor (o agregarlo al archivo hosts). Sin eso no hay forma de conectar.",

        { TcpConecta: false } =>
            $"El nombre '{Host}' sí resuelve ({string.Join(", ", Ips)}) pero el puerto {Puerto} no acepta conexión. " +
            $"PEDIR A SOPORTE: abrir la salida TCP del servidor hacia {string.Join(", ", Ips)}:{Puerto} " +
            $"(firewall perimetral y/o Firewall de Windows).",

        { Alcanzable: true } =>
            $"Conectividad correcta hacia {Host}:{Puerto}. Si la operación igual falla, el problema NO es de red: " +
            "revisar credenciales, permisos o la respuesta de la aplicación remota.",

        _ => $"No se pudo determinar el estado de {Host}:{Puerto}."
    };
}

/// <summary>
/// Sondea destinos de red desde el servidor. Nunca lanza: ante cualquier problema devuelve el
/// sondeo con el fallo descrito, porque esto se usa justamente dentro de los manejadores de error.
/// </summary>
public interface IDiagnosticoRed
{
    /// <summary>Sondea el host y puerto que implica una URL.</summary>
    Task<SondeoDestino> SondearUrl(string destino, string url, CancellationToken ct = default);

    /// <summary>Sondea un host y puerto explícitos (FTP, SQL, lo que sea).</summary>
    Task<SondeoDestino> SondearHost(string destino, string host, int puerto, CancellationToken ct = default);
}
