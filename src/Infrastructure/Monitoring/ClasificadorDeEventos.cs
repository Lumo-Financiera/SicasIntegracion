using LumoSys.Integraciones.Domain.Shared.Errores;
using Sentry;

namespace LumoSys.Integraciones.Infrastructure.Monitoring;

/// <summary>
/// Decide qué severidad merece cada evento y si debe notificar.
///
/// Vive aparte del arranque por dos motivos. El primero es de responsabilidades: configurar el SDK
/// es una cosa y decidir qué es grave es otra, y esto segundo es una regla de negocio —la de
/// observabilidad— que conviene poder leer y probar sin levantar la aplicación. El segundo es
/// práctico: estas reglas se van a ajustar con el tiempo, y cada ajuste debe poder verificarse.
///
/// El criterio de fondo, que explica todas las reglas de abajo: <b>lo que afecta a un registro se
/// registra, lo que afecta a todos alerta</b>. Un documento que no sube es un aviso; el FTP que
/// rechaza todos los documentos es una alerta.
/// </summary>
public static class ClasificadorDeEventos
{
    /// <summary>
    /// Valores de la etiqueta <c>alerta</c>, que es sobre la que se configuran las reglas de
    /// notificación en Sentry. Se usa la etiqueta y no el nivel para que cambiar el criterio de
    /// severidad no obligue a reescribir cada regla en la consola.
    /// </summary>
    public const string AlertaCritica     = "critica";
    public const string AlertaInformativa = "informativa";

    /// <summary>
    /// Ajusta el nivel del evento y le pone la etiqueta <c>alerta</c>.
    ///
    /// Se aplica en <c>BeforeSend</c>, es decir a TODOS los eventos y después de que ya se
    /// generaron: así ningún punto del código puede olvidarse de clasificar —incluidos los que
    /// llegan por el puente de <c>ILogger</c>, que convierte en evento cada <c>LogError</c>— y
    /// ninguna de estas reglas puede alterar el flujo de la operación.
    /// </summary>
    public static SentryEvent Clasificar(SentryEvent evento)
    {
        // Un evento que ya viene marcado como crítico desde el código manda: lo pone quien conoce
        // la consecuencia real, que desde aquí no se puede deducir. Un 530 del FTP se ve igual
        // tanto si falló un documento como si fallaron los trescientos.
        if (evento.Tags.TryGetValue("alerta", out string? marcado) && marcado == AlertaCritica)
        {
            evento.Level = SentryLevel.Fatal;
            return evento;
        }

        SentryLevel nivel = NivelSegun(evento.Exception)
                            ?? evento.Level
                            ?? SentryLevel.Error;

        evento.Level = nivel;
        evento.SetTag("alerta", nivel is SentryLevel.Fatal or SentryLevel.Error
            ? AlertaCritica
            : AlertaInformativa);

        return evento;
    }

    /// <summary>
    /// Nivel que merece una excepción por su tipo, o null si no hay nada que decir de ella y vale
    /// el nivel con el que venía.
    /// </summary>
    public static SentryLevel? NivelSegun(Exception? ex) => ex switch
    {
        null => null,

        // Defectos de programación y agotamiento de recursos: siempre despiertan a alguien, porque
        // no se arreglan solos ni reintentando.
        NullReferenceException or ArgumentNullException or IndexOutOfRangeException
            or InvalidCastException or OutOfMemoryException
            => SentryLevel.Fatal,

        // La base de datos es el destino de todo el ETL: si falla, no se guarda nada.
        Microsoft.Data.SqlClient.SqlException
            or Microsoft.EntityFrameworkCore.DbUpdateException
            => SentryLevel.Fatal,

        // Un servicio externo que contesta "con esta petición no puedo" afecta a un registro; uno
        // que no contesta, o contesta 5xx, deja fuera a todos.
        ErrorDeIntegracion integracion
            => integracion.AfectaSoloAEsteRegistro ? SentryLevel.Warning : SentryLevel.Error,

        // Reglas de negocio y catálogos: quedan registrados para auditoría, no notifican.
        // Va DESPUÉS de las ramas anteriores porque hereda de InvalidOperationException.
        ErrorDeNegocio => SentryLevel.Warning,

        // Caídas de red hacia los servicios externos. El reintento ya absorbe las momentáneas, así
        // que si una llega hasta aquí es que persistió.
        HttpRequestException or System.Net.Sockets.SocketException or IOException
            => SentryLevel.Error,

        // Una agregada vale lo que su peor componente.
        AggregateException agregada when agregada.InnerExceptions.Count > 0
            => agregada.InnerExceptions.Select(NivelSegun).Max() ?? SentryLevel.Error,

        _ => null
    };
}
