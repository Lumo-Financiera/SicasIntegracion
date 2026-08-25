using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Infrastructure.Persistence;
using LumoSys.Integraciones.Infrastructure.Persistence.Models;

namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Escribe en LOG_ERRORES de dbLumoSys — mismo destino que usa el resto de LumoSys
/// (slnLumoSys.Controllers.* y varios SP_*) para su propio log de errores. Antes escribía en
/// dbIntegraciones (tabla Seguridad.Bitacora), decomisionado a petición del usuario.
///
/// Reparto de responsabilidades entre los dos destinos:
/// - **Archivo diario**: recibe SIEMPRE todo, con folio y detalle. Es la fuente para diagnosticar.
/// - **LOG_ERRORES**: recibe la señal, con supresión de repetidos. Es una tabla compartida por
///   todo LumoSys, así que un error en bucle del ETL le tapa los errores a los demás módulos.</summary>
public sealed class BitacoraRepository(
    LumoSysContext db,
    LogErroresArchivoService logArchivo,
    SupresorErroresRepetidos supresor) : IBitacoraRepository
{
    public async Task GuardarAsync(
        string descripcion, NivelBitacora nivel, int idAplicacion, CancellationToken ct = default)
    {
        // El archivo se lleva todo, sin filtrar: es donde se reconstruye qué pasó exactamente.
        await logArchivo.EscribirAsync($"[{nivel}] (Aplicacion {idAplicacion}) {descripcion}", ct);

        var decision = supresor.Evaluar(descripcion);
        if (!decision.Escribir)
            return;

        string modulo = idAplicacion switch
        {
            11 => "Seguros",
            12 => "Siniestros",
            _  => idAplicacion.ToString()
        };

        string mensaje = $"[{nivel}] {descripcion}";

        // Al reabrirse la ventana de supresión se informa cuántas repeticiones se callaron,
        // para no perder la magnitud real del problema en la tabla.
        if (decision.VecesSuprimidas > 0)
            mensaje += $" (+{decision.VecesSuprimidas} repeticiones omitidas; ver log de archivo)";

        db.LogErrores.Add(new LogErroresModel
        {
            LER_ORIGEN         = $"LumoSys.Integraciones.{modulo}",
            LER_NO_ERROR       = 0,
            LER_MENSAJE_ERROR  = mensaje.Length > 4000 ? mensaje[..4000] : mensaje,
            LER_TLG_ID         = 2, // PROCESO AUTOMATICO (catálogo TIPOS_LOG) — asunción, ver CLAUDE.md
            LER_REVISADO       = false,
            LER_FECHA_REGISTRO = DateTime.Now
        });

        await db.SaveChangesAsync(ct);
    }
}
