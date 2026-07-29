namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Log diario en texto plano, mismo patrón que los ETL legacy
/// (C:\Desarrollo\Programas\PROD\Integraciones\Logs\Seguros|Siniestros\Log dd-MM-yyyy.txt):
/// un archivo por día, se va agregando línea por línea conforme ocurren avisos/errores.</summary>
public sealed class LogErroresArchivoService
{
    private const string CarpetaBase = @"C:\LumoSys\Programas\Sicas";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task EscribirAsync(string mensaje, CancellationToken ct = default)
    {
        Directory.CreateDirectory(CarpetaBase);
        string archivo = Path.Combine(CarpetaBase, $"Log {DateTime.Now:dd-MM-yyyy}.txt");
        string linea = $"{DateTime.Now:dd/MM/yyyy HH:mm:ss} {mensaje}{Environment.NewLine}";

        await Lock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(archivo, linea, ct);
        }
        finally
        {
            Lock.Release();
        }
    }
}
