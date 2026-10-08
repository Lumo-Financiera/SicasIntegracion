namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

/// <summary>
/// Describe una corrida programada del ETL para el monitoreo. Los BackgroundServices arman este
/// descriptor una sola vez al arrancar y lo reutilizan en cada ciclo.
/// </summary>
public sealed record DescriptorCorridaEtl
{
    /// <summary>Identificador estable del monitor programado en Sentry (slug en minúsculas y
    /// guiones, ej. <c>etl-seguros-diario</c>). Debe NO cambiar entre despliegues: es la llave con
    /// la que Sentry sabe que una corrida esperada no llegó.</summary>
    public required string Monitor { get; init; }

    /// <summary>Nombre legible de la transacción en Sentry Performance (ej. "ETL Seguros (diario)").</summary>
    public required string Nombre { get; init; }

    /// <summary>Seguros | Siniestros.</summary>
    public required string Modulo { get; init; }

    /// <summary>programado-diario | programado-intervalo | manual.</summary>
    public required string Disparador { get; init; }

    /// <summary>Expresión crontab del horario esperado (ej. <c>"5 0 * * *"</c> para las 00:05).
    /// Se registra en Sentry junto con el check-in, así el monitor queda dado de alta sin
    /// configurarlo a mano en la consola. Mutuamente excluyente con <see cref="IntervaloMinutos"/>.</summary>
    public string? Crontab { get; init; }

    /// <summary>Alternativa a <see cref="Crontab"/> para los barridos de ventana angosta:
    /// cada cuántos minutos se espera una corrida.</summary>
    public int? IntervaloMinutos { get; init; }

    /// <summary>Minutos tras los cuales una corrida que no cerró se considera colgada.</summary>
    public int? MaxDuracionMinutos { get; init; }

    /// <summary>Tolerancia en minutos antes de dar por ausente una corrida esperada
    /// (absorbe el arranque del servicio y el drift de reloj del servidor).</summary>
    public int? MargenMinutos { get; init; }
}

/// <summary>
/// Monitoreo de las corridas programadas del ETL. Además de capturar errores, reporta a Sentry
/// que la corrida ocurrió — esto es lo que permite detectar el caso "el servicio de Windows está
/// caído y nadie se enteró", que no genera ninguna excepción y por lo tanto ningún log
/// (ver CLAUDE.md: la póliza L0000019660-0 se perdió exactamente así).
/// </summary>
public interface IMonitoreoEtl
{
    /// <summary>Abre una corrida: ámbito aislado + transacción de rendimiento + aviso de inicio al
    /// monitor programado. Debe consumirse con <c>using</c> y cerrarse con
    /// <see cref="ICorridaEtl.MarcarExito"/> / <see cref="ICorridaEtl.MarcarFallo"/>.</summary>
    ICorridaEtl IniciarCorrida(DescriptorCorridaEtl descriptor);
}

/// <summary>Corrida en curso. Liberarla sin marcar resultado equivale a
/// <see cref="MarcarExito"/> (el ciclo terminó sin excepción).</summary>
public interface ICorridaEtl : IDisposable
{
    /// <summary>Etiqueta que aplica a todos los eventos de esta corrida (ej. la ventana consultada).</summary>
    void Etiquetar(string clave, string? valor);

    void MarcarExito();

    void MarcarFallo(Exception ex);

    /// <summary>Cierre por paro del servicio: no cuenta como fallo del monitor programado, para no
    /// generar alertas falsas en cada reinicio o despliegue.</summary>
    void MarcarCancelada();
}
