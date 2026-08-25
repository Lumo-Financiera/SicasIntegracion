namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Configuración del log de archivo (sección "LogArchivo" de appsettings).</summary>
public sealed class LogArchivoOptions
{
    /// <summary>Carpeta destino. Se crea sola si no existe.</summary>
    public string Carpeta { get; init; } = @"C:\LumoSys\Programas\Sicas";

    /// <summary>Días de archivos a conservar. Los más viejos se borran al arrancar. 0 = sin límite.</summary>
    public int RetencionDias { get; init; } = 90;

    /// <summary>Tope de líneas en cola sin escribir. Al llenarse se descartan las más viejas —
    /// nunca se bloquea el ETL ni se deja crecer la memoria sin límite (ver incidente OOM).</summary>
    public int CapacidadCola { get; init; } = 20_000;

    /// <summary>Incluir la categoría (clase que emitió el log) en cada línea.</summary>
    public bool IncluirCategoria { get; init; } = true;

    /// <summary>Ventana en minutos para suprimir errores idénticos repetidos. 0 = no suprimir.
    /// Existe porque el modo intervalo reprocesa las mismas pólizas fallidas cada N minutos:
    /// una sola póliza llegó a generar 3,231 filas en LOG_ERRORES en 60 días.</summary>
    public int SupresionMinutos { get; init; } = 360;
}
