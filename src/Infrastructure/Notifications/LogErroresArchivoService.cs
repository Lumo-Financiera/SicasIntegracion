using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Log diario en texto plano, mismo patrón que los ETL legacy
/// (C:\Desarrollo\Programas\PROD\Integraciones\Logs\Seguros|Siniestros\Log dd-MM-yyyy.txt):
/// un archivo por día, se va agregando línea por línea.
///
/// Dos rutas de escritura, a propósito:
/// - <see cref="EscribirAsync"/> escribe de inmediato. Es para errores y avisos de bitácora:
///   bajo volumen, y se quiere garantía de que quedan en disco incluso si el proceso muere.
/// - <see cref="Encolar"/> pasa por una cola en memoria que vacía una tarea de fondo. Es la vía
///   del <see cref="ArchivoLoggerProvider"/>, que recibe todo el ILogger del proyecto: alto
///   volumen, y no debe frenar al ETL esperando el disco.
/// Ambas comparten el mismo semáforo, así que nunca escriben encimadas.</summary>
public sealed class LogErroresArchivoService : IAsyncDisposable
{
    private readonly LogArchivoOptions _opts;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Channel<string> _cola;
    private readonly Task _consumidor;
    private readonly CancellationTokenSource _cts = new();

    public LogErroresArchivoService(IOptions<LogArchivoOptions> opts)
    {
        _opts = opts.Value;

        // DropOldest: si el consumidor se atrasa, se pierden líneas viejas antes que crecer sin
        // límite. Preferimos perder log que tumbar el servicio por memoria.
        _cola = Channel.CreateBounded<string>(new BoundedChannelOptions(_opts.CapacidadCola)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        _consumidor = Task.Run(() => ConsumirAsync(_cts.Token));
        AplicarRetencion();
    }

    /// <summary>Escritura inmediata (bitácora de errores, excepciones no controladas).</summary>
    public async Task EscribirAsync(string mensaje, CancellationToken ct = default)
    {
        string linea = $"{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff} {mensaje}";
        await _lock.WaitAsync(ct);
        try
        {
            await AppendAsync(linea, ct);
        }
        catch
        {
            // El log no debe tumbar el proceso: si el disco falla, se pierde la línea y se sigue.
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Encola una línea ya formateada. No bloquea; si la cola está llena descarta la más vieja.</summary>
    public void Encolar(string linea) => _cola.Writer.TryWrite(linea);

    private async Task ConsumirAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string linea in _cola.Reader.ReadAllAsync(ct))
            {
                await _lock.WaitAsync(ct);
                try   { await AppendAsync(linea, ct); }
                catch { /* ver EscribirAsync */ }
                finally { _lock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* apagado normal */ }
    }

    private async Task AppendAsync(string linea, CancellationToken ct)
    {
        Directory.CreateDirectory(_opts.Carpeta);
        string archivo = Path.Combine(_opts.Carpeta, $"Log {DateTime.Now:dd-MM-yyyy}.txt");
        await File.AppendAllTextAsync(archivo, linea + Environment.NewLine, ct);
    }

    /// <summary>Borra los archivos diarios más viejos que RetencionDias. Antes de esto el
    /// historial crecía sin límite en el disco del servidor.</summary>
    private void AplicarRetencion()
    {
        if (_opts.RetencionDias <= 0) return;

        try
        {
            if (!Directory.Exists(_opts.Carpeta)) return;
            DateTime corte = DateTime.Now.Date.AddDays(-_opts.RetencionDias);

            foreach (string ruta in Directory.EnumerateFiles(_opts.Carpeta, "Log *.txt"))
            {
                string nombre = Path.GetFileNameWithoutExtension(ruta); // "Log dd-MM-yyyy"
                if (nombre.Length < 14) continue;

                if (DateTime.TryParseExact(nombre[4..], "dd-MM-yyyy",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime fecha)
                    && fecha < corte)
                {
                    File.Delete(ruta);
                }
            }
        }
        catch
        {
            // Limpiar el historial es best-effort: nunca debe impedir el arranque del servicio.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cierra la cola y deja que el consumidor termine de vaciarla antes de apagar,
        // para no perder las últimas líneas al detener el servicio.
        _cola.Writer.TryComplete();
        try
        {
            await Task.WhenAny(_consumidor, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch { /* ignorado */ }

        await _cts.CancelAsync();
        _cts.Dispose();
        _lock.Dispose();
    }
}
