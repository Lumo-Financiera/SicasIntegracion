using System.Collections.Concurrent;
using System.Reflection;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LumoSys.Integraciones.API.Filters;

/// <summary>
/// Etiqueta cada petición con el controlador, la acción y los identificadores de negocio que
/// vengan en sus argumentos (póliza, serie, folio de siniestro…), para que un error reportado
/// desde cualquier endpoint diga <i>sobre qué registro</i> ocurrió y no solo en qué ruta.
///
/// Se registra una sola vez como filtro global (<c>Program.cs</c>) en lugar de instrumentar cada
/// acción: los controllers siguen siendo thin, y cualquier endpoint que se agregue después queda
/// cubierto sin tocar nada.
///
/// Las etiquetas se normalizan a los mismos nombres que usan los handlers del ETL
/// (<c>poliza</c>, <c>serie</c>, <c>folio_siniestro</c>…), así un mismo registro se puede rastrear
/// en Sentry sin importar si entró por el barrido automático o por la API.
/// </summary>
public sealed class MonitoreoActionFilter(IMonitoreoErrores monitoreo) : IActionFilter
{
    /// <summary>
    /// Lista blanca: nombre del parámetro o propiedad → nombre de etiqueta canónico. Es
    /// deliberadamente una lista blanca y no "todo lo que traiga el modelo" — los comandos
    /// incluyen datos personales (beneficiario, contratante, ejecutivo) que no tienen por qué
    /// salir hacia un servicio externo.
    /// </summary>
    private static readonly Dictionary<string, string> EtiquetasDeNegocio = new(StringComparer.OrdinalIgnoreCase)
    {
        ["poliza"]         = "poliza",
        ["serie"]          = "serie",
        ["noserie"]        = "serie",
        ["inciso"]         = "inciso",
        ["noreporte"]      = "folio_siniestro",
        ["numreporte"]     = "folio_siniestro",
        ["reporte"]        = "folio_siniestro",
        ["foliosiniestro"] = "folio_siniestro",
        ["nosiniestro"]    = "no_siniestro",
        ["idsiniestro"]    = "idsiniestro",
        ["iddocto"]        = "iddocto"
    };

    /// <summary>La reflexión se resuelve una vez por tipo de comando, no por petición.</summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropiedadesPorTipo = new();

    public void OnActionExecuting(ActionExecutingContext context)
    {
        monitoreo.Etiquetar("controlador", context.RouteData.Values["controller"]?.ToString());
        monitoreo.Etiquetar("accion", context.RouteData.Values["action"]?.ToString());

        foreach (var (nombre, valor) in context.ActionArguments)
        {
            if (valor is null) continue;

            // Parámetro suelto de ruta o query (ej. {serie}, ?noReporte=).
            if (EsValorSimple(valor.GetType()))
            {
                if (EtiquetasDeNegocio.TryGetValue(nombre, out string? etiqueta))
                    monitoreo.Etiquetar(etiqueta, valor.ToString());
                continue;
            }

            // Comando enlazado desde el cuerpo: se leen solo las propiedades de la lista blanca.
            foreach (var prop in ObtenerPropiedades(valor.GetType()))
            {
                object? contenido = prop.GetValue(valor);
                if (contenido is null) continue;

                monitoreo.Etiquetar(EtiquetasDeNegocio[prop.Name], contenido.ToString());
            }
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    private static PropertyInfo[] ObtenerPropiedades(Type tipo) =>
        PropiedadesPorTipo.GetOrAdd(tipo, static t =>
            [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .Where(p => p.CanRead
                          && EsValorSimple(p.PropertyType)
                          && EtiquetasDeNegocio.ContainsKey(p.Name))]);

    private static bool EsValorSimple(Type tipo)
    {
        Type real = Nullable.GetUnderlyingType(tipo) ?? tipo;
        return real == typeof(string) || real.IsPrimitive || real.IsEnum || real == typeof(decimal);
    }
}
