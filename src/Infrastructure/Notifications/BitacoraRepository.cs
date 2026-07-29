using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Infrastructure.Persistence;
using LumoSys.Integraciones.Infrastructure.Persistence.Models;

namespace LumoSys.Integraciones.Infrastructure.Notifications;

/// <summary>Escribe en LOG_ERRORES de dbLumoSys — mismo destino que usa el resto de LumoSys
/// (slnLumoSys.Controllers.* y varios SP_*) para su propio log de errores. Antes escribía en
/// dbIntegraciones (tabla Seguridad.Bitacora), decomisionado a petición del usuario.</summary>
public sealed class BitacoraRepository(LumoSysContext db, LogErroresArchivoService logArchivo) : IBitacoraRepository
{
    public async Task GuardarAsync(
        string descripcion, NivelBitacora nivel, int idAplicacion, CancellationToken ct = default)
    {
        await logArchivo.EscribirAsync($"[{nivel}] (Aplicacion {idAplicacion}) {descripcion}", ct);

        string modulo = idAplicacion switch
        {
            11 => "Seguros",
            12 => "Siniestros",
            _  => idAplicacion.ToString()
        };

        string mensaje = $"[{nivel}] {descripcion}";

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
