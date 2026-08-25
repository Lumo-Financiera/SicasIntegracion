using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Evita que el mismo error se escriba una y otra vez en LOG_ERRORES.
///
/// El modo intervalo reprocesa la misma ventana cada N minutos, así que una póliza que falla por
/// un catálogo faltante vuelve a fallar en cada corrida. Medido el 24/08/2026: 32 pólizas
/// generaron 5,449 filas en 60 días — una sola de ellas, 3,231. Como LOG_ERRORES es la tabla
/// compartida por todo LumoSys, ese ruido entierra los errores de los demás módulos.
///
/// Singleton: el estado tiene que sobrevivir entre corridas (los handlers son scoped).</summary>
public sealed class SupresorErroresRepetidos(IOptions<LogArchivoOptions> opts)
{
    private readonly int _ventanaMinutos = opts.Value.SupresionMinutos;
    private readonly ConcurrentDictionary<string, Registro> _vistos = new();

    private sealed record Registro(DateTime Primera, DateTime Ultima, int Veces);

    /// <summary>Resultado de evaluar si un mensaje debe persistirse.</summary>
    /// <param name="Escribir">true si toca escribirlo en la bitácora.</param>
    /// <param name="VecesSuprimidas">Cuántas repeticiones se callaron desde la última escritura
    /// (0 la primera vez). Se anexa al mensaje para no perder la magnitud del problema.</param>
    public readonly record struct Decision(bool Escribir, int VecesSuprimidas);

    public Decision Evaluar(string mensaje)
    {
        if (_ventanaMinutos <= 0)
            return new Decision(true, 0);

        string clave = Normalizar(mensaje);
        DateTime ahora = DateTime.Now;

        while (true)
        {
            if (!_vistos.TryGetValue(clave, out var actual))
            {
                if (_vistos.TryAdd(clave, new Registro(ahora, ahora, 1)))
                    return new Decision(true, 0);
                continue; // otro hilo lo insertó primero: reevaluar
            }

            bool venció = (ahora - actual.Ultima).TotalMinutes >= _ventanaMinutos;

            var siguiente = venció
                ? new Registro(ahora, ahora, 1)
                : actual with { Veces = actual.Veces + 1 };

            if (_vistos.TryUpdate(clave, siguiente, actual))
            {
                // Al vencer la ventana se vuelve a escribir, informando cuántas se callaron.
                return venció
                    ? new Decision(true, actual.Veces - 1)
                    : new Decision(false, actual.Veces);
            }
        }
    }

    /// <summary>Quita números y fechas para que dos ocurrencias del mismo error se agrupen aunque
    /// cambie el folio o la hora. Sin esto, cada póliza contaría como un error distinto y la
    /// supresión no serviría contra el bucle real (siempre las mismas 32 pólizas).</summary>
    private static string Normalizar(string mensaje)
    {
        var sb = new System.Text.StringBuilder(mensaje.Length);
        bool digitoPrevio = false;

        foreach (char c in mensaje)
        {
            if (char.IsDigit(c))
            {
                if (!digitoPrevio) sb.Append('#');
                digitoPrevio = true;
            }
            else
            {
                sb.Append(c);
                digitoPrevio = false;
            }
        }

        return sb.ToString();
    }
}
