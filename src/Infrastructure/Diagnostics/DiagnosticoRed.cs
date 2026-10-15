using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.Infrastructure.Diagnostics;

/// <summary>
/// Mide, desde el propio servidor, si un destino se puede alcanzar: resuelve el nombre y abre un
/// socket al puerto. Nada más — no habla el protocolo de la aplicación, porque lo que se quiere
/// distinguir es exactamente eso: "no resuelve" (DNS), "no conecta" (firewall/puerto) y
/// "conecta pero la aplicación rechaza" (credenciales, permisos).
///
/// Los tiempos de espera son cortos a propósito: esto corre DENTRO de manejadores de error, y un
/// diagnóstico que tarde más que la operación que falló sería peor que no tenerlo.
/// </summary>
public sealed class DiagnosticoRed(ILogger<DiagnosticoRed> log) : IDiagnosticoRed
{
    private static readonly TimeSpan TiempoLimiteDns = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TiempoLimiteTcp = TimeSpan.FromSeconds(5);

    public async Task<SondeoDestino> SondearUrl(string destino, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new SondeoDestino(destino, url, url, 0, [], false, null, null,
                "La URL configurada no es válida.");

        return await SondearHost(destino, uri.Host, uri.Port, ct, url);
    }

    public Task<SondeoDestino> SondearHost(string destino, string host, int puerto, CancellationToken ct = default)
        => SondearHost(destino, host, puerto, ct, $"{host}:{puerto}");

    private async Task<SondeoDestino> SondearHost(
        string destino, string host, int puerto, CancellationToken ct, string urlMostrada)
    {
        string[] ips;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TiempoLimiteDns);

            var direcciones = await Dns.GetHostAddressesAsync(host, cts.Token);
            ips = direcciones.Select(d => d.ToString()).ToArray();
        }
        catch (Exception ex)
        {
            // Sin DNS no tiene caso probar el puerto: no hay a dónde conectarse.
            return new SondeoDestino(destino, urlMostrada, host, puerto, [], false, null, null,
                $"Fallo de DNS: {ex.Message}");
        }

        if (ips.Length == 0)
            return new SondeoDestino(destino, urlMostrada, host, puerto, [], false, null, null,
                "El DNS no devolvió ninguna dirección.");

        var reloj = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TiempoLimiteTcp);

            using var socket = new TcpClient();
            await socket.ConnectAsync(ips[0], puerto, cts.Token);
            reloj.Stop();

            return new SondeoDestino(destino, urlMostrada, host, puerto, ips, true, true,
                reloj.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            reloj.Stop();
            log.LogDebug(ex, "Sondeo TCP fallido hacia {Host}:{Puerto}", host, puerto);

            return new SondeoDestino(destino, urlMostrada, host, puerto, ips, true, false, null,
                $"No se pudo abrir el socket: {ex.Message}");
        }
    }
}
