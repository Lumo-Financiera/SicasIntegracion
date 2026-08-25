using System.Diagnostics;

namespace LumoSys.Integraciones.Domain.Shared.Models;

/// <summary>Acumula qué pasó durante una corrida del ETL para poder cerrarla con una sola línea
/// de resumen. Antes de esto el log no decía nunca cuántos registros se habían procesado,
/// omitido o fallado: había que inferirlo leyendo línea por línea, o no se sabía.
///
/// No es thread-safe por diseño: el ETL procesa un registro a la vez (foreach con await).</summary>
public sealed class ResumenLote(string modulo, string modo)
{
    // Nombres de contador. Constantes para que un typo no cree un contador fantasma.
    public const string Paginas            = "paginas";
    public const string Leidos             = "leidos";
    public const string Procesados         = "procesados";
    public const string OmitidosFlotilla   = "omitidos_flotilla";
    public const string OmitidosSinDatos   = "omitidos_sin_datos";
    public const string Errores            = "errores";
    public const string DocsSubidos        = "docs_subidos";
    public const string DocsFallidos       = "docs_fallidos";
    public const string DocsSinVincular    = "docs_sin_vincular";
    public const string SFleetOk           = "sfleet_ok";
    public const string SFleetFallo        = "sfleet_fallo";
    public const string ComentariosLeidos  = "comentarios_leidos";
    public const string ComentariosNuevos  = "comentarios_nuevos";
    public const string ComentariosYaExistian = "comentarios_ya_existian";
    /// <summary>Comentarios de SICAS que no se pudieron ligar a ningún siniestro porque
    /// SIN_FOLIO_SICAS está NULL. Es el contador más importante del módulo de Siniestros:
    /// este caso se descartaba en silencio y por eso el defecto pasó meses sin detectarse.</summary>
    public const string ComentariosSinVinculo = "comentarios_sin_vinculo";

    private readonly Dictionary<string, int> _contadores = [];
    private readonly Stopwatch _cronometro = Stopwatch.StartNew();

    /// <summary>Id corto para correlacionar todas las líneas de esta corrida en el archivo.</summary>
    public string CorrelacionId { get; } = Guid.NewGuid().ToString("N")[..6];

    public string Modulo => modulo;
    public string Modo   => modo;
    public TimeSpan Duracion => _cronometro.Elapsed;

    public void Inc(string contador, int cuantos = 1) =>
        _contadores[contador] = _contadores.GetValueOrDefault(contador) + cuantos;

    public int Obtener(string contador) => _contadores.GetValueOrDefault(contador);

    /// <summary>Línea final del lote. Solo lista los contadores con valor, para que el resumen
    /// se lea de un golpe en vez de mostrar quince ceros.</summary>
    public string Resumir()
    {
        var partes = _contadores
            .Where(p => p.Value != 0)
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");

        string detalle = string.Join(" ", partes);
        if (detalle.Length == 0) detalle = "sin actividad";

        return $"RESUMEN {modulo} ({modo}) en {Duracion.TotalSeconds:F1}s :: {detalle}";
    }

    /// <summary>true si algo salió mal y vale la pena que el resumen suba a nivel Warning.</summary>
    public bool TieneIncidencias =>
        Obtener(Errores) > 0 ||
        Obtener(DocsFallidos) > 0 ||
        Obtener(ComentariosSinVinculo) > 0 ||
        Obtener(SFleetFallo) > 0 ||
        Obtener(DocsSinVincular) > 0;
}
