using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Models;
using LumoSys.Integraciones.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace LumoSys.Integraciones.Infrastructure.Persistence.Repositories;

public sealed class SiniestroRepository(LumoSysContext db) : ISiniestroRepository
{
    private const byte TMO_ID_SINIESTROS = 11;
    private const string ESTATUS_SOLICITUD = "SOLICITUD";

    /// <summary>Traducción SICAS IdUser -> USUARIOS.USU_ID (EjecutivoSICAS del legacy).
    /// Tabla de mapeo manual — no existe catálogo ni regla que la derive.</summary>
    private static readonly Dictionary<int, int> EjecutivoSicasMap = new()
    {
        [14] = 1982, [20] = 1982, [10] = 1989, [9] = 1977, [8] = 1866,
        [3] = 416, [27] = 1777, [26] = 2211, [37] = 2209, [38] = 2260,
        [43] = 2301, [33] = 2179, [54] = 2412, [57] = 2430, [58] = 2432
    };
    private const int EjecutivoSicasDefault = 416;

    public async Task<bool> ExisteVehiculoAsync(string serie, CancellationToken ct = default) =>
        (await ResolverCdeIdAsync(serie, ct)).HasValue;

    /// <summary>SIN_ID no es identity — se calcula manualmente. UPDLOCK+HOLDLOCK sobre la
    /// tabla completa evita que dos inserts concurrentes (ej. esta instancia y el servicio
    /// corriendo en el servidor) calculen el mismo siguiente ID.</summary>
    private async Task<int> ObtenerSiguienteSinIdAsync(CancellationToken ct)
    {
        var resultado = await db.Database
            .SqlQuery<int>($"SELECT ISNULL(MAX(SIN_ID), 0) + 1 AS Value FROM SINIESTROS WITH (UPDLOCK, HOLDLOCK)")
            .ToListAsync(ct);

        return resultado[0];
    }

    private async Task<int?> ResolverCdeIdAsync(string serie, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serie))
            return null;

        return await (
            from cd in db.ComprasDetalles
            join c in db.Compras on cd.CDE_COM_ID equals c.COM_ID
            where cd.CDE_NO_SERIE == serie
                  && (c.COM_TES_ID == 2 || cd.CDE_COMPLETO)
                  && c.COM_TES_ID != 3
            select (int?)cd.CDE_ID
        ).FirstOrDefaultAsync(ct);
    }

    public async Task<int> UpsertSiniestroAsync(DatosSiniestro datos, CancellationToken ct = default)
    {
        using var tx = await db.Database.BeginTransactionAsync(ct);

        int cdeId = await ResolverCdeIdAsync(datos.NoSerie ?? string.Empty, ct)
            ?? throw new InvalidOperationException(
                $"La serie '{datos.NoSerie}' no está registrada como vehículo propio en COMPRAS_DETALLES.");

        int tsiId = await ResolverTipoSiniestroIdAsync(datos.TipoSiniestro, ct);
        byte torId = await ResolverTipoOrigenIdAsync(ct);
        int segId = await ResolverPolizaIdAsync(datos.NumeroPoliza, datos.Inciso, ct);

        var existente = await db.Siniestros
            .FirstOrDefaultAsync(x => x.SIN_CDE_ID == cdeId && x.SIN_NO_REPORTE == datos.NoReporte, ct);

        if (existente is null)
        {
            // SIN_ID no es identity ni tiene default/secuencia en BD (confirmado: sin
            // sys.identity_columns, sin default constraint, sin SEQUENCE dedicada — a
            // diferencia de ARC_ID que sí usa SEQ_ARC_ID/SP_ACTUALIZAR_SECUENCIAS). Se calcula
            // aquí con MAX+1 bajo UPDLOCK/HOLDLOCK dentro de la misma transacción para evitar
            // colisiones con otros procesos que insertan en SINIESTROS.
            int nuevoId = await ObtenerSiguienteSinIdAsync(ct);

            existente = new SiniestrosModel
            {
                SIN_ID             = nuevoId,
                SIN_CDE_ID         = cdeId,
                SIN_NO_REPORTE     = datos.NoReporte,
                SIN_USU_ID         = 3,
                SIN_FECHA_REGISTRO = DateTime.Now
            };
            db.Siniestros.Add(existente);
        }

        existente.SIN_SEG_ID               = segId;
        existente.SIN_TSI_ID               = tsiId;
        existente.SIN_TOR_ID               = torId;
        existente.SIN_NO_SINIESTRO         = datos.NoSiniestro;
        // Único campo que trae la bitácora H03314011 para vincular sus comentarios a este
        // siniestro (NumReporte/folio no viene en esa respuesta) — se rellena también en updates
        // para siniestros ya existentes que se crearon antes de este fix.
        existente.SIN_FOLIO_SICAS          = datos.IDSiniestro;
        existente.SIN_FECHA_EVENTO         = datos.FechaEvento;
        existente.SIN_FECHA_RESOLUCION     = datos.FechaResolucion;
        existente.SIN_DESCRIPCION          = datos.Descripcion;
        existente.SIN_MONTO_INDEMNIZABLE   = datos.MontoIndemnizable;
        existente.SIN_MONTO_DEDUCIBLE       = null; // el ETL legacy nunca lo llena
        existente.SIN_MONTO_PRIMAS_PENDIENTES = datos.MontoPrimasPendientes;
        existente.SIN_MONTO_OTROS_DESCUENTOS  = datos.MontoOtrosDescuentos;
        existente.SIN_PAGO_DEDUCIBLE_CLIENTE  = null;
        existente.SIN_VRE_ID                  = null; // sin lógica de llenado en el legacy

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return existente.SIN_ID;
    }

    /// <summary>Replica ValidarPoliza: por SEG_NO_POLIZA; si hay varias, desambigua por
    /// SDE_INCISO (fallback inciso 1); prioriza SEG_ACTIVO, si no el SEG_ID más antiguo.</summary>
    private async Task<int> ResolverPolizaIdAsync(string numeroPoliza, int? inciso, CancellationToken ct)
    {
        var polizas = await db.Seguros.Where(x => x.SEG_NO_POLIZA == numeroPoliza).ToListAsync(ct);
        if (polizas.Count == 0)
            throw new InvalidOperationException(
                $"La Póliza '{numeroPoliza}' no se encuentra registrada en SEGUROS.");

        if (polizas.Count == 1)
            return polizas[0].SEG_ID;

        var segIds = polizas.Select(p => p.SEG_ID).ToList();
        int incisoBuscado = inciso is null or 0 ? 1 : inciso.Value;

        var candidatos = await db.SegurosDetalles
            .Where(x => segIds.Contains(x.SDE_SEG_ID) && x.SDE_INCISO == incisoBuscado)
            .Select(x => x.SDE_SEG_ID)
            .ToListAsync(ct);

        if (candidatos.Count == 0 && incisoBuscado != 1)
        {
            candidatos = await db.SegurosDetalles
                .Where(x => segIds.Contains(x.SDE_SEG_ID) && x.SDE_INCISO == 1)
                .Select(x => x.SDE_SEG_ID)
                .ToListAsync(ct);
        }

        var elegibles = candidatos.Count > 0
            ? polizas.Where(p => candidatos.Contains(p.SEG_ID)).ToList()
            : polizas;

        return (elegibles.FirstOrDefault(p => p.SEG_ACTIVO) ?? elegibles.OrderBy(p => p.SEG_ID).First()).SEG_ID;
    }

    private async Task<int> ResolverTipoSiniestroIdAsync(string? valor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(valor))
            throw new InvalidOperationException("El Tipo de Siniestro es obligatorio.");

        string texto = valor.ToUpper();
        int? id = await db.TiposSiniestros.Where(x => x.TSI_DESCRIPCION == texto).Select(x => (int?)x.TSI_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"El Tipo de Siniestro '{valor}' no se encuentra en el catálogo TIPOS_SINIESTROS.");
    }

    private async Task<byte> ResolverTipoOrigenIdAsync(CancellationToken ct)
    {
        byte? id = await db.TiposOrigenes.Where(x => x.TOR_DESCRIPCION == "SISTEMA").Select(x => (byte?)x.TOR_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException("No se encontró 'SISTEMA' en el catálogo TIPOS_ORIGENES.");
    }

    private async Task<int> ResolverTipoEstatusIdAsync(string? valor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(valor))
            throw new InvalidOperationException("El Estatus del siniestro es obligatorio.");

        string texto = valor.ToUpper();
        int? id = await db.TiposEstatus
            .Where(x => x.TES_DESCRIPCION == texto && x.TES_TMO_ID == TMO_ID_SINIESTROS)
            .Select(x => (int?)x.TES_ID).FirstOrDefaultAsync(ct);

        return id ?? throw new InvalidOperationException(
            $"El Estatus '{valor}' no se encuentra en el catálogo TIPOS_ESTATUS (TES_TMO_ID={TMO_ID_SINIESTROS}).");
    }

    /// <summary>Replica la resolución de SES_USU_ID: en "SOLICITUD" se busca por nombre de ejecutivo
    /// contra USUARIOS; en cualquier otro estatus se usa la tabla fija EjecutivoSICAS (IdUser SICAS).</summary>
    private async Task<int> ResolverUsuarioEstatusIdAsync(string? estatus, string? ejecutivo, int idUserSicas, CancellationToken ct)
    {
        if (string.Equals(estatus, ESTATUS_SOLICITUD, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(ejecutivo))
                throw new InvalidOperationException("Ejecutivo requerido para estatus SOLICITUD.");

            string buscado = ejecutivo.Trim().ToUpper();
            var usuarios = await db.Usuarios
                .Select(x => new { x.USU_ID, Nombre = (x.USU_NOMBRE + " " + x.USU_APELLIDO_PATERNO + " " + x.USU_APELLIDO_MATERNO) })
                .ToListAsync(ct);

            var match = usuarios.FirstOrDefault(x => x.Nombre.Contains(buscado));
            return match?.USU_ID ?? throw new InvalidOperationException(
                $"El Ejecutivo '{ejecutivo}' no se encontró registrado en el catálogo USUARIOS.");
        }

        return EjecutivoSicasMap.GetValueOrDefault(idUserSicas, EjecutivoSicasDefault);
    }

    public async Task<bool> ExisteEstatusAsync(
        int siniestroId, string comentarios, DateTime fechaRegistro, CancellationToken ct = default)
    {
        return await db.SiniestrosEstatus
            .AnyAsync(x =>
                x.SES_SIN_ID == siniestroId &&
                x.SES_COMENTARIOS == comentarios &&
                x.SES_FECHA_REGISTRO == fechaRegistro, ct);
    }

    public async Task UpsertEstatusAsync(DatosEstatus datos, int siniestroId, CancellationToken ct = default)
    {
        int tesId = await ResolverTipoEstatusIdAsync(datos.Estatus, ct);
        int usuId = await ResolverUsuarioEstatusIdAsync(datos.Estatus, datos.Ejecutivo, datos.IdUser, ct);
        DateTime fechaRegistro = datos.FechaRegistro ?? DateTime.Now;

        var existente = await db.SiniestrosEstatus
            .FirstOrDefaultAsync(x =>
                x.SES_SIN_ID == siniestroId &&
                x.SES_COMENTARIOS == datos.Comentarios &&
                x.SES_FECHA_REGISTRO == fechaRegistro, ct);

        if (existente is null)
        {
            db.SiniestrosEstatus.Add(new SiniestrosEstatusModel
            {
                SES_SIN_ID         = siniestroId,
                SES_TES_ID         = tesId,
                SES_COMENTARIOS    = datos.Comentarios,
                SES_FECHA_REGISTRO = fechaRegistro,
                SES_USU_ID         = usuId
            });
        }
        else
        {
            existente.SES_TES_ID      = tesId;
            existente.SES_USU_ID      = usuId;
            existente.SES_COMENTARIOS = datos.Comentarios;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ExisteDocumentoAsync(
        int siniestroId, string nombreBase, CancellationToken ct = default)
    {
        return await db.DocumentosSiniestros
            .AnyAsync(x =>
                x.DSI_SIN_ID == siniestroId &&
                x.DSI_ARCHIVO.StartsWith(nombreBase + "_"), ct);
    }

    public async Task RegistrarDocumentoAsync(
        int siniestroId, string nombreArchivo, CancellationToken ct = default)
    {
        db.DocumentosSiniestros.Add(new DocumentosSiniestrosModel
        {
            DSI_SIN_ID         = siniestroId,
            DSI_ARCHIVO        = nombreArchivo,
            DSI_FECHA_REGISTRO = DateTime.Now,
            DSI_USU_ID         = 3
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<int?> BuscarIdPorReporte(string noReporte, CancellationToken ct = default)
    {
        var sin = await db.Siniestros
            .FirstOrDefaultAsync(x => x.SIN_NO_REPORTE == noReporte, ct);
        return sin?.SIN_ID;
    }

    public async Task<int?> BuscarIdPorFolioSicas(int idSiniestroSicas, CancellationToken ct = default)
    {
        var sin = await db.Siniestros
            .FirstOrDefaultAsync(x => x.SIN_FOLIO_SICAS == idSiniestroSicas, ct);
        return sin?.SIN_ID;
    }
}
