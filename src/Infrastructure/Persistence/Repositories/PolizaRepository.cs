using System.Globalization;
using System.Text;
using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Seguros.Models;
using LumoSys.Integraciones.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace LumoSys.Integraciones.Infrastructure.Persistence.Repositories;

public sealed class PolizaRepository(LumoSysContext db) : IPolizaRepository
{
    private const int ESTATUS_VIGENTE = 177;
    private const int ESTATUS_SUSTITUCION = 497;
    private const int ESTATUS_PENDIENTE = 454;

    public async Task<bool> ExisteVehiculoAsync(string serie, CancellationToken ct = default)
    {
        var (cdeId, vhcId) = await ResolverOrigenVehiculoAsync(serie, ct);
        return cdeId.HasValue || vhcId.HasValue;
    }

    public async Task<int> UpsertPolizaAsync(DatosPoliza datos, CancellationToken ct = default)
    {
        using var tx = await db.Database.BeginTransactionAsync(ct);

        byte aseguradoraId = await ResolverAseguradoraIdAsync(datos.Aseguradora, ct);
        byte? formaPagoId = await ResolverFormaPagoIdAsync(datos.FormaPago, ct);
        var (beneficiarioId, beneficiarioPreferente) = await ResolverBeneficiarioAsync(datos.Beneficiario, datos.BeneficiarioPreferente, ct);
        byte? empresaId = await ResolverEmpresaIdAsync(datos.Contratante, ct);

        var existente = await db.Seguros
            .FirstOrDefaultAsync(x =>
                x.SEG_NO_POLIZA == datos.NumeroPoliza &&
                x.SEG_ASE_ID == aseguradoraId &&
                x.SEG_ACTIVO, ct);

        if (existente is null)
        {
            existente = new SegurosModel
            {
                SEG_NO_POLIZA      = datos.NumeroPoliza,
                SEG_ASE_ID         = aseguradoraId,
                SEG_BPR_ID         = beneficiarioId,
                SEG_BENEFICIARIO_PREFERENTE = beneficiarioPreferente,
                SEG_BROKER         = datos.Broker,
                SEG_TFS_ID         = formaPagoId,
                SEG_EMP_ID         = empresaId,
                SEG_ACTIVO         = true,
                SEG_USU_ID         = 3,
                SEG_FECHA_REGISTRO = DateTime.Now.Date
            };
            db.Seguros.Add(existente);
        }
        else
        {
            existente.SEG_BPR_ID = beneficiarioId;
            existente.SEG_BENEFICIARIO_PREFERENTE = beneficiarioPreferente;
            existente.SEG_BROKER = datos.Broker;
            existente.SEG_TFS_ID = formaPagoId;
            existente.SEG_EMP_ID = empresaId;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return existente.SEG_ID;
    }

    public async Task<int> UpsertVehiculoAsync(DatosVehiculo datos, int polizaId, CancellationToken ct = default)
    {
        using var tx = await db.Database.BeginTransactionAsync(ct);

        var (cdeId, vhcId) = await ResolverOrigenVehiculoAsync(datos.Serie ?? string.Empty, ct);

        var existente = cdeId.HasValue
            ? await db.SegurosDetalles.FirstOrDefaultAsync(x => x.SDE_CDE_ID == cdeId && x.SDE_SEG_ID == polizaId, ct)
            : vhcId.HasValue
                ? await db.SegurosDetalles.FirstOrDefaultAsync(x => x.SDE_VHC_ID == vhcId && x.SDE_SEG_ID == polizaId, ct)
                : null;

        bool esNuevo = existente is null;
        if (esNuevo)
        {
            existente = new SegurosDetallesModel
            {
                SDE_SEG_ID = polizaId,
                SDE_CDE_ID = cdeId,
                SDE_VHC_ID = vhcId,
                SDE_TES_ID = ESTATUS_SUSTITUCION // placeholder inicial, ReasignarEstatusAsync lo corrige
            };
            db.SegurosDetalles.Add(existente);
        }

        existente.SDE_INCISO              = datos.Inciso is null or 0 ? 1 : datos.Inciso;
        existente.SDE_IVA                 = datos.IVA;
        existente.SDE_PRIMER_PAGO         = datos.PrimerRecibo;
        existente.SDE_PAGO_SUBSECUENTE    = datos.Subsecuente;
        existente.SDE_PRIMA_NETA          = datos.PrimaNeta;
        existente.SDE_RECARGO_FRACCIONADO = datos.Recargos;
        existente.SDE_DERECHO_EXPEDICION  = datos.Derechos;
        existente.SDE_FECHA_CONTRATACION  = datos.FechaInicio;
        existente.SDE_FECHA_VENCIMIENTO   = datos.FechaVencimiento;
        existente.SDE_PRIMA_TOTAL         = datos.PrimaTotal;
        existente.SDE_DESCRIPCION_ADAPTACION = datos.Adaptacion;
        existente.SDE_VALOR_ADAPTACION    = datos.ValorAdaptacion;
        existente.SDE_VALOR_FACTURA       = datos.ValorFactura;
        existente.SDE_DEDUCIBLE_DANIOS       = datos.DeducibleDanos;
        existente.SDE_DEDUCIBLE_ROBO_TOTAL   = datos.DeducibleRoboTotal;
        existente.SDE_DEDUCIBLE_ROBO_PARCIAL = datos.DeducibleRoboParcial;
        existente.SDE_DEDUCIBLE_RC           = datos.DeducibleResponsabilidadCivil;
        existente.SDE_ACC_CONDUCTOR       = datos.AccidentesConductor;
        existente.SDE_GM_OCUPANTES        = datos.GastosMedicosOcupantes;
        existente.SDE_RC_LUC              = datos.ResponsabilidadCivilLimiteUnicoCombinado;

        existente.SDE_TVS_ID_DANIOS      = await ResolverTipoValorSeguroIdAsync(datos.CoberturasDanosMateriales, ct);
        existente.SDE_TVS_ID_ROBO_TOTAL  = await ResolverTipoValorSeguroIdAsync(datos.CoberturasRoboTotal, ct);
        existente.SDE_AMPARO_ROBO_PARCIAL = ResolverCoberturaBooleana(datos.RoboParcial);
        existente.SDE_AMPARO_ADAPTACIONES = ResolverCoberturaBooleana(datos.AdaptacionesConversiones);
        existente.SDE_AMPARO_JURIDICO     = ResolverCoberturaBooleana(datos.AsistenciaJuridica);
        existente.SDE_AMPARO_VIAL         = ResolverCoberturaBooleana(datos.AsistenciaVial);
        existente.SDE_AMPARO_RC_CRUZADA   = ResolverCoberturaBooleana(datos.ResponsabilidadCivilCruzada);
        existente.SDE_AMPARO_RC_PASAJEROS = ResolverCoberturaBooleana(datos.ResponsabilidadCivilPasajeros);
        existente.SDE_AMPARO_RC_EXTRANJERO = ResolverCoberturaBooleana(datos.ResponsabilidadCivilExtranjero);
        existente.SDE_AMPARO_RC_ADICIONALES = null;
        existente.SDE_OTROS               = null;
        existente.SDE_VALOR_COMERCIAL     = null;
        existente.SDE_MONTO_DEDUCIBLE     = null;

        existente.SDE_TTC_ID = await ResolverTipoAdministracionIdAsync(datos.AdministracionCartera, ct);
        existente.SDE_TUS_ID = await ResolverTipoUsoIdAsync(datos.TipoUso, ct);
        existente.SDE_TNS_ID = await ResolverPasajerosIdAsync(datos.Pasajeros, ct);
        existente.SDE_TPZ_ID = await ResolverTipoPolizaIdAsync(datos.TipoPoliza, ct);
        existente.SDE_TCX_ID = await ResolverCoberturaIdAsync(datos.Cobertura, ct);
        existente.SDE_TGS_ID = await ResolverGestionPagoIdAsync(datos.GestionPago, ct);
        existente.SDE_USU_ID_EJECUTIVO = await ResolverEjecutivoIdAsync(datos.Ejecutivo, ct);

        await db.SaveChangesAsync(ct);
        await ReasignarEstatusAsync(existente, datos.TipoActivo, ct);
        await tx.CommitAsync(ct);
        return existente.SDE_ID;
    }

    // ─── Resolución de origen del vehículo (ValidaOrigen / ExisteVehiculo) ─────

    private async Task<(int? CdeId, int? VhcId)> ResolverOrigenVehiculoAsync(string serie, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serie))
            return (null, null);

        var compra = await (
            from cd in db.ComprasDetalles
            join c in db.Compras on cd.CDE_COM_ID equals c.COM_ID
            where cd.CDE_NO_SERIE == serie
                  && (c.COM_TES_ID == 2 || cd.CDE_COMPLETO)
                  && c.COM_TES_ID != 3
            select cd
        ).FirstOrDefaultAsync(ct);

        if (compra is not null)
            return (compra.CDE_ID, null);

        var vehiculo = await db.Vehiculos.FirstOrDefaultAsync(x => x.VHC_NO_SERIE == serie, ct);
        return vehiculo is not null ? (null, vehiculo.VHC_ID) : (null, null);
    }

    // ─── Reasignación de estatus (ReasignaEstatus) ─────────────────────────────

    private async Task ReasignarEstatusAsync(SegurosDetallesModel detalle, string tipoActivo, CancellationToken ct)
    {
        if (detalle.SDE_CDE_ID is not null)
        {
            var otras = await db.SegurosDetalles
                .Where(x => x.SDE_CDE_ID == detalle.SDE_CDE_ID && x.SDE_SEG_ID != detalle.SDE_SEG_ID)
                .OrderBy(x => x.SDE_FECHA_VENCIMIENTO)
                .ToListAsync(ct);

            if (otras.Count == 0)
            {
                detalle.SDE_TES_ID = ESTATUS_VIGENTE;
                await db.SaveChangesAsync(ct);
                return;
            }

            foreach (var anterior in otras)
            {
                anterior.SDE_TES_ID = ESTATUS_SUSTITUCION;
                await db.SaveChangesAsync(ct);

                int vigentesRestantes = await db.SegurosDetalles
                    .CountAsync(x => x.SDE_SEG_ID == anterior.SDE_SEG_ID && x.SDE_TES_ID == ESTATUS_VIGENTE, ct);

                if (vigentesRestantes == 0)
                {
                    var polizaAnterior = await db.Seguros.FirstOrDefaultAsync(x => x.SEG_ID == anterior.SDE_SEG_ID, ct);
                    if (polizaAnterior is not null)
                    {
                        polizaAnterior.SEG_ACTIVO = false;
                        await db.SaveChangesAsync(ct);
                    }
                }
            }

            if (tipoActivo == "RENOVACIÓN")
            {
                var vigentesFuturas = await db.SegurosDetalles
                    .Where(x => x.SDE_CDE_ID == detalle.SDE_CDE_ID && x.SDE_SEG_ID != detalle.SDE_SEG_ID && x.SDE_FECHA_VENCIMIENTO >= DateTime.Now)
                    .OrderBy(x => x.SDE_FECHA_VENCIMIENTO)
                    .ToListAsync(ct);

                if (vigentesFuturas.Count > 0)
                {
                    detalle.SDE_TES_ID = ESTATUS_PENDIENTE;

                    var ultimoRegistrado = await db.SegurosDetalles
                        .Where(x => x.SDE_CDE_ID == detalle.SDE_CDE_ID && x.SDE_SEG_ID != detalle.SDE_SEG_ID)
                        .OrderByDescending(x => x.SDE_FECHA_VENCIMIENTO)
                        .FirstOrDefaultAsync(ct);

                    if (ultimoRegistrado is not null)
                        ultimoRegistrado.SDE_TES_ID = ESTATUS_VIGENTE;
                }
                else
                {
                    detalle.SDE_TES_ID = ESTATUS_VIGENTE;
                }
            }
            else // SUSTITUCIÓN (default)
            {
                detalle.SDE_TES_ID = ESTATUS_VIGENTE;
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        if (detalle.SDE_VHC_ID is not null)
        {
            var relacionados = await db.SegurosDetalles
                .Where(x => x.SDE_VHC_ID == detalle.SDE_VHC_ID)
                .OrderBy(x => x.SDE_FECHA_VENCIMIENTO)
                .ToListAsync(ct);

            foreach (var anterior in relacionados.Where(x => x.SDE_ID != detalle.SDE_ID))
            {
                anterior.SDE_TES_ID = ESTATUS_SUSTITUCION;
            }

            var ultimo = relacionados
                .OrderByDescending(x => x.SDE_FECHA_VENCIMIENTO)
                .FirstOrDefault() ?? detalle;
            ultimo.SDE_TES_ID = ESTATUS_VIGENTE;

            await db.SaveChangesAsync(ct);
        }
    }

    // ─── Catálogos: Seguros ─────────────────────────────────────────────────────

    private async Task<byte> ResolverAseguradoraIdAsync(string? aseguradora, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(aseguradora))
            throw new InvalidOperationException("La Aseguradora es obligatoria.");

        string valor = aseguradora.ToUpper();
        byte? id = await db.Aseguradoras.Where(x => x.ASE_DESCRIPCION == valor).Select(x => (byte?)x.ASE_ID).FirstOrDefaultAsync(ct);

        return id ?? throw new InvalidOperationException(
            $"La Aseguradora '{aseguradora}' no se encuentra registrada en el catálogo ASEGURADORAS.");
    }

    private async Task<byte?> ResolverFormaPagoIdAsync(string? formaPago, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(formaPago))
            return null;

        string valor = formaPago.Trim();
        byte? id = await db.TiposFormasPagosSeguros.Where(x => x.TFS_DESCRIPCION == valor).Select(x => (byte?)x.TFS_ID).FirstOrDefaultAsync(ct);

        return id ?? throw new InvalidOperationException(
            $"La Forma de Pago '{formaPago}' no se encuentra registrada en el catálogo TIPOS_FORMAS_PAGOS_SEGUROS.");
    }

    /// <summary>Replica ValidarBeneficiarios: preferente explícito → match exacto → normalizar acentos y reintentar → "PENDIENTE" literal → texto libre.</summary>
    private async Task<(byte? BeneficiarioId, string? BeneficiarioPreferente)> ResolverBeneficiarioAsync(
        string? beneficiario, string? beneficiarioPreferente, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(beneficiarioPreferente))
            return (null, beneficiarioPreferente);

        if (string.IsNullOrEmpty(beneficiario))
            return (null, null);

        byte? id = await db.BeneficiariosPreferentes.Where(x => x.BPR_DESCRIPCION == beneficiario).Select(x => (byte?)x.BPR_ID).FirstOrDefaultAsync(ct);
        if (id.HasValue)
            return (id, null);

        string sinAcentos = QuitarAcentos(beneficiario);
        if (sinAcentos == "PENDIENTE")
            return (null, "PENDIENTE");

        var catalogo = await db.BeneficiariosPreferentes.Where(x => x.BPR_DESCRIPCION == sinAcentos)
            .Select(x => new { x.BPR_ID, x.BPR_DESCRIPCION }).FirstOrDefaultAsync(ct);
        if (catalogo is not null)
            return ((byte?)catalogo.BPR_ID, catalogo.BPR_DESCRIPCION);

        return (null, beneficiario);
    }

    private static string QuitarAcentos(string texto)
    {
        var normalizado = texto.Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder();
        foreach (char c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                resultado.Append(c);
        }
        return resultado.ToString();
    }

    /// <summary>Replica ValidarEmpresa: cascada RAZON_SOCIAL → NOMBRE_CORTO → DESCRIPCION. Opcional, sin error si no hay match.</summary>
    private async Task<byte?> ResolverEmpresaIdAsync(string? contratante, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(contratante))
            return null;

        string valor = contratante.Trim();

        byte? id = await db.Empresas.Where(x => x.EMP_RAZON_SOCIAL == valor).Select(x => (byte?)x.EMP_ID).FirstOrDefaultAsync(ct);
        id ??= await db.Empresas.Where(x => x.EMP_NOMBRE_CORTO == valor).Select(x => (byte?)x.EMP_ID).FirstOrDefaultAsync(ct);
        id ??= await db.Empresas.Where(x => x.EMP_DESCRIPCION == valor).Select(x => (byte?)x.EMP_ID).FirstOrDefaultAsync(ct);

        return id;
    }

    // ─── Catálogos: SegurosDetalles ─────────────────────────────────────────────

    private async Task<byte?> ResolverTipoAdministracionIdAsync(string? valor, CancellationToken ct)
    {
        if (valor is null)
            return null;

        byte? id = await db.TiposAdministracionesCartera.Where(x => x.TTC_DESCRIPCION == valor).Select(x => (byte?)x.TTC_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"El Tipo de Administración de Cartera '{valor}' no se encuentra en el catálogo TIPOS_ADMINISTRACIONES_CARTERA.");
    }

    private async Task<byte?> ResolverTipoUsoIdAsync(string? valor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(valor))
            return null;

        string texto = valor.Trim();
        byte? id = await db.TiposUsos.Where(x => x.TUS_DESCRIPCION == texto).Select(x => (byte?)x.TUS_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"El Tipo de Uso '{valor}' no se encuentra en el catálogo TIPOS_USOS.");
    }

    /// <summary>Replica ValidarPasajeros: si no existe, da de alta el catálogo automáticamente.</summary>
    private async Task<byte?> ResolverPasajerosIdAsync(string? valor, CancellationToken ct)
    {
        if (valor is null)
            return null;

        var existente = await db.TiposNoPasajeros.Where(x => x.TNS_DESCRIPCION == valor).Select(x => new { x.TNS_ID }).FirstOrDefaultAsync(ct);
        if (existente is not null)
            return (byte)existente.TNS_ID;

        var nuevo = new TiposNoPasajerosModel { TNS_DESCRIPCION = valor };
        db.TiposNoPasajeros.Add(nuevo);
        await db.SaveChangesAsync(ct);
        return (byte)nuevo.TNS_ID;
    }

    private async Task<byte?> ResolverTipoPolizaIdAsync(string? valor, CancellationToken ct)
    {
        if (valor is null)
            return null;

        string texto = valor.Trim();
        byte? id = await db.TiposPolizas.Where(x => x.TPZ_DESCRIPCION == texto).Select(x => (byte?)x.TPZ_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"El Tipo de Póliza '{valor}' no se encuentra en el catálogo TIPOS_POLIZAS.");
    }

    private async Task<byte?> ResolverCoberturaIdAsync(string? valor, CancellationToken ct)
    {
        if (valor is null)
            return null;

        string texto = valor.Trim();
        byte? id = await db.TiposCoberturas.Where(x => x.TCX_DESCRIPCION == texto).Select(x => (byte?)x.TCX_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"La Cobertura '{valor}' no se encuentra en el catálogo TIPOS_COBERTURAS.");
    }

    private async Task<byte?> ResolverGestionPagoIdAsync(string? valor, CancellationToken ct)
    {
        if (valor is null)
            return null;

        string texto = valor.Trim();
        byte? id = await db.TiposGestionPagosSeguro.Where(x => x.TGS_DESCRIPCION == texto).Select(x => (byte?)x.TGS_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"La Gestión de Pago '{valor}' no se encuentra en el catálogo TIPOS_GESTION_PAGOS_SEGURO.");
    }

    /// <summary>Replica ValidarTipoValorSeguro: si el valor es literalmente "AMPARADA" se sustituye por "VALOR FACTURA" antes de buscar.</summary>
    private async Task<byte?> ResolverTipoValorSeguroIdAsync(string? valor, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(valor))
            return null;

        string texto = valor == "AMPARADA" ? "VALOR FACTURA" : valor;
        texto = texto.Trim();

        byte? id = await db.TiposValoresSeguro.Where(x => x.TVS_DESCRIPCION == texto).Select(x => (byte?)x.TVS_ID).FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"El Tipo de Valor de Seguro '{valor}' no se encuentra en el catálogo TIPOS_VALORES_SEGURO.");
    }

    private async Task<int?> ResolverEjecutivoIdAsync(string? ejecutivo, CancellationToken ct)
    {
        if (ejecutivo is null)
            return null;

        string buscado = ejecutivo.Trim().ToUpper();
        var usuarios = await db.Usuarios
            .Select(x => new { x.USU_ID, Nombre = (x.USU_APELLIDO_PATERNO + " " + x.USU_APELLIDO_MATERNO + " " + x.USU_NOMBRE) })
            .ToListAsync(ct);

        var match = usuarios.FirstOrDefault(x => x.Nombre.Contains(buscado));
        return match?.USU_ID ?? throw new InvalidOperationException(
            $"El Ejecutivo '{ejecutivo}' no se encontró registrado en el catálogo USUARIOS.");
    }

    /// <summary>Replica ValidarCoberturaPoliza: el valor debe ser AMPARADA/AMPARADO/NO APLICA. No usa catálogo.</summary>
    private static bool? ResolverCoberturaBooleana(string? valor)
    {
        if (valor is null)
            return null;

        string texto = valor.ToUpper();
        return texto switch
        {
            "AMPARADA" or "AMPARADO" => true,
            "NO APLICA" => false,
            _ => throw new InvalidOperationException($"El valor de cobertura '{valor}' debe ser AMPARADA, AMPARADO o NO APLICA.")
        };
    }

    // ─── Documentos ──────────────────────────────────────────────────────────

    public async Task<bool> ExisteDocumentoAsync(string serie, string nombreArchivo, CancellationToken ct = default)
    {
        return await db.DocumentosUnidades
            .Join(db.ArchivosRepositorios,
                du => du.DUN_ARC_ID,
                ar => ar.ARC_ID,
                (du, ar) => new { du, ar })
            .AnyAsync(x =>
                x.du.DUN_TDW_ID == 4 &&
                x.ar.ARC_NOMBRE_ARCHIVO.Contains(nombreArchivo), ct);
    }

    public async Task<int> RegistrarArchivoAsync(string nombreArchivo, long tamanoBytes, CancellationToken ct = default)
    {
        int arcId = await ObtenerSecuenciaAsync("ARC", ct);

        var nuevoArchivo = new ArchivosRepositoriosModel
        {
            ARC_ID             = arcId,
            ARC_REP_ID         = 34, // Documentos Unidades
            ARC_NOMBRE_ARCHIVO = nombreArchivo,
            ARC_NO_BYTES       = tamanoBytes,
            ARC_TMM_ID         = 2, // PDF
            ARC_USU_ID         = 3,
            ARC_FECHA_REGISTRO = DateTime.Now
        };

        db.ArchivosRepositorios.Add(nuevoArchivo);
        await db.SaveChangesAsync(ct);
        return nuevoArchivo.ARC_ID;
    }

    public async Task VincularDocumentoUnidadAsync(string serie, int archivoId, CancellationToken ct = default)
    {
        // DUN_CDE_ID y DUN_CLI_ID son NOT NULL en el esquema real y no existe un equivalente a
        // "vehículo externo" (solo en VEHICULOS, sin CDE_ID) para esta tabla — a diferencia de
        // SEGUROS_DETALLES, aquí no hay fallback posible. El archivo ya quedó subido al FTP;
        // si no se puede vincular, se omite (limitación conocida del esquema, no un error).
        var compra = await db.ComprasDetalles.FirstOrDefaultAsync(x => x.CDE_NO_SERIE == serie, ct);
        if (compra is null)
            return;

        var c = await db.Compras.FirstOrDefaultAsync(x => x.COM_ID == compra.CDE_COM_ID, ct);
        if (c?.COM_CLI_ID is not int cliId)
            return;

        var doc = new DocumentosUnidadesModel
        {
            DUN_CDE_ID         = compra.CDE_ID,
            DUN_ARC_ID         = archivoId,
            DUN_CLI_ID         = cliId,
            DUN_TDW_ID         = 4,
            DUN_USU_ID         = 3,
            DUN_PUBLICO        = true,
            DUN_FECHA_REGISTRO = DateTime.Now
        };

        db.DocumentosUnidades.Add(doc);
        await db.SaveChangesAsync(ct);
    }

    public Task<int> ObtenerSecuenciaAsync(string tabla, CancellationToken ct = default) =>
        db.ObtenerSecuenciaAsync(tabla, ct);
}
