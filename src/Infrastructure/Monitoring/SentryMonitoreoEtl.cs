using System.Diagnostics;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Sentry;

namespace LumoSys.Integraciones.Infrastructure.Monitoring;

/// <summary>
/// Implementación de <see cref="IMonitoreoEtl"/> sobre Sentry. Cada corrida del ETL abre tres
/// cosas a la vez, todas cerradas por <see cref="ICorridaEtl"/>:
///
/// 1. Un <b>ámbito aislado</b> (scope): los errores del ciclo salen etiquetados con módulo y
///    disparador, y el contexto de un ciclo no se filtra al siguiente.
/// 2. Una <b>transacción de rendimiento</b>: da la duración real de cada barrido en Sentry
///    Performance (equivalente a lo que en lumo-system hacen
///    <c>Context.StartSentryTransaction()</c> / <c>FinishSentryTransaction()</c> por request,
///    trasladado a un proceso de fondo que no tiene request).
/// 3. Un <b>check-in de monitor programado</b> (Sentry Crons): avisa que la corrida empezó y con
///    qué resultado terminó. Es la única señal capaz de detectar la ausencia de corridas — un
///    servicio de Windows detenido no lanza excepciones ni escribe logs, así que sin check-ins
///    el silencio es indistinguible del funcionamiento normal.
/// </summary>
public sealed class SentryMonitoreoEtl : IMonitoreoEtl
{
    public ICorridaEtl IniciarCorrida(DescriptorCorridaEtl descriptor) => new CorridaEtlSentry(descriptor);
}

internal sealed class CorridaEtlSentry : ICorridaEtl
{
    private readonly DescriptorCorridaEtl _descriptor;
    private readonly IDisposable _ambito;
    private readonly ITransactionTracer _transaccion;
    private readonly SentryId _checkIn;
    private readonly Stopwatch _cronometro;
    private bool _cerrada;

    internal CorridaEtlSentry(DescriptorCorridaEtl descriptor)
    {
        _descriptor = descriptor;
        _cronometro = Stopwatch.StartNew();

        _ambito = SentrySdk.PushScope();

        _checkIn = SentrySdk.CaptureCheckIn(
            descriptor.Monitor,
            CheckInStatus.InProgress,
            configureMonitorOptions: opciones => ConfigurarMonitor(opciones, descriptor));

        // "etl.run" agrupa estas transacciones aparte de las HTTP y es la marca que usa el
        // TracesSampler para muestrearlas al 100% (ver SentryStartupExtensions).
        _transaccion = SentrySdk.StartTransaction(descriptor.Nombre, "etl.run");

        SentrySdk.ConfigureScope(scope =>
        {
            scope.Transaction = _transaccion;
            scope.SetTag("modulo", descriptor.Modulo);
            scope.SetTag("disparador", descriptor.Disparador);
            scope.SetTag("monitor", descriptor.Monitor);
        });
    }

    public void Etiquetar(string clave, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return;
        _transaccion.SetTag(clave, valor);
        SentrySdk.ConfigureScope(scope => scope.SetTag(clave, valor));
    }

    public void MarcarExito() => Cerrar(CheckInStatus.Ok, () => _transaccion.Finish(SpanStatus.Ok));

    public void MarcarFallo(Exception ex) => Cerrar(CheckInStatus.Error, () => _transaccion.Finish(ex));

    /// <summary>Paro del servicio: se cierra la transacción como cancelada y el check-in como
    /// correcto — un reinicio o despliegue no es una corrida fallida y no debe alertar.</summary>
    public void MarcarCancelada() => Cerrar(CheckInStatus.Ok, () => _transaccion.Finish(SpanStatus.Cancelled));

    /// <summary>Si nadie marcó resultado, el ciclo terminó sin excepción: se reporta correcto.</summary>
    public void Dispose() => Cerrar(CheckInStatus.Ok, () => _transaccion.Finish(SpanStatus.Ok));

    private void Cerrar(CheckInStatus estado, Action finalizarTransaccion)
    {
        if (_cerrada) return;
        _cerrada = true;

        _cronometro.Stop();
        finalizarTransaccion();

        SentrySdk.CaptureCheckIn(
            _descriptor.Monitor,
            estado,
            _checkIn,
            duration: _cronometro.Elapsed,
            configureMonitorOptions: opciones => ConfigurarMonitor(opciones, _descriptor));

        _ambito.Dispose();
    }

    /// <summary>Da de alta (o actualiza) el monitor programado desde el propio código, para que el
    /// horario esperado viva junto a la configuración del ETL y no se desincronice de la consola
    /// de Sentry al cambiar <c>EtlSchedule</c>.</summary>
    private static void ConfigurarMonitor(SentryMonitorOptions opciones, DescriptorCorridaEtl descriptor)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.Crontab))
            opciones.Interval(descriptor.Crontab);
        else if (descriptor.IntervaloMinutos is > 0)
            opciones.Interval(descriptor.IntervaloMinutos.Value, SentryMonitorInterval.Minute);

        if (descriptor.MaxDuracionMinutos is > 0)
            opciones.MaxRuntime = TimeSpan.FromMinutes(descriptor.MaxDuracionMinutos.Value);

        if (descriptor.MargenMinutos is > 0)
            opciones.CheckInMargin = TimeSpan.FromMinutes(descriptor.MargenMinutos.Value);

        // Hora del servidor donde corre el servicio de Windows: sin esto Sentry evalúa el horario
        // esperado en UTC y marcaría como atrasada cada corrida nocturna.
        opciones.TimeZone = TimeZoneInfo.Local.Id;
    }
}
