using LumoSys.Integraciones.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace LumoSys.Integraciones.Infrastructure.Persistence;

public sealed class LumoSysContext(DbContextOptions<LumoSysContext> options) : DbContext(options)
{
    // Seguros
    public DbSet<SegurosModel> Seguros => Set<SegurosModel>();
    public DbSet<SegurosDetallesModel> SegurosDetalles => Set<SegurosDetallesModel>();
    public DbSet<ArchivosRepositoriosModel> ArchivosRepositorios => Set<ArchivosRepositoriosModel>();
    public DbSet<DocumentosUnidadesModel> DocumentosUnidades => Set<DocumentosUnidadesModel>();
    public DbSet<ComprasDetallesModel> ComprasDetalles => Set<ComprasDetallesModel>();
    public DbSet<ComprasModel> Compras => Set<ComprasModel>();
    public DbSet<VehiculosModel> Vehiculos => Set<VehiculosModel>();

    // Catálogos de Seguros
    public DbSet<AseguradorasModel> Aseguradoras => Set<AseguradorasModel>();
    public DbSet<TiposFormasPagosSegurosModel> TiposFormasPagosSeguros => Set<TiposFormasPagosSegurosModel>();
    public DbSet<BeneficiariosPreferentesModel> BeneficiariosPreferentes => Set<BeneficiariosPreferentesModel>();
    public DbSet<EmpresasModel> Empresas => Set<EmpresasModel>();
    public DbSet<TiposAdministracionesCarteraModel> TiposAdministracionesCartera => Set<TiposAdministracionesCarteraModel>();
    public DbSet<TiposUsosModel> TiposUsos => Set<TiposUsosModel>();
    public DbSet<TiposNoPasajerosModel> TiposNoPasajeros => Set<TiposNoPasajerosModel>();
    public DbSet<TiposPolizasModel> TiposPolizas => Set<TiposPolizasModel>();
    public DbSet<TiposCoberturasModel> TiposCoberturas => Set<TiposCoberturasModel>();
    public DbSet<TiposGestionPagosSeguroModel> TiposGestionPagosSeguro => Set<TiposGestionPagosSeguroModel>();
    public DbSet<TiposValoresSeguroModel> TiposValoresSeguro => Set<TiposValoresSeguroModel>();
    public DbSet<UsuariosModel> Usuarios => Set<UsuariosModel>();

    // Siniestros
    public DbSet<SiniestrosModel> Siniestros => Set<SiniestrosModel>();
    public DbSet<SiniestrosEstatusModel> SiniestrosEstatus => Set<SiniestrosEstatusModel>();
    public DbSet<DocumentosSiniestrosModel> DocumentosSiniestros => Set<DocumentosSiniestrosModel>();

    // Catálogos de Siniestros
    public DbSet<TiposSiniestrosModel> TiposSiniestros => Set<TiposSiniestrosModel>();
    public DbSet<TiposOrigenesModel> TiposOrigenes => Set<TiposOrigenesModel>();
    public DbSet<TiposEstatusModel> TiposEstatus => Set<TiposEstatusModel>();

    // Log de errores (compartido con el resto de LumoSys)
    public DbSet<LogErroresModel> LogErrores => Set<LogErroresModel>();

    /// <summary>Firma real confirmada vía INFORMATION_SCHEMA.PARAMETERS: @TABLA varchar (IN),
    /// @ID int (IN), @FolioSQ int (INOUT) — nombres y tipos distintos a lo que este método asumía
    /// antes (causaba "expects the parameter '@Numero', which was not supplied").</summary>
    public async Task<int> ObtenerSecuenciaAsync(string tabla, CancellationToken ct = default)
    {
        var paramTabla = new Microsoft.Data.SqlClient.SqlParameter("@TABLA", System.Data.SqlDbType.VarChar) { Value = tabla };
        var paramId    = new Microsoft.Data.SqlClient.SqlParameter("@ID", System.Data.SqlDbType.Int) { Value = 0 };
        var paramFolio = new Microsoft.Data.SqlClient.SqlParameter("@FolioSQ", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.InputOutput,
            Value     = 0
        };

        await Database.ExecuteSqlRawAsync(
            "EXEC SP_ACTUALIZAR_SECUENCIAS @TABLA, @ID, @FolioSQ OUTPUT",
            [paramTabla, paramId, paramFolio], ct);

        return (int)(paramFolio.Value ?? 0);
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SegurosModel>(e =>
        {
            // SEGUROS tiene triggers en BD: SQL Server no permite OUTPUT sin INTO sobre tablas con triggers.
            e.ToTable("SEGUROS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.SEG_ID);
        });

        mb.Entity<SegurosDetallesModel>(e =>
        {
            e.ToTable("SEGUROS_DETALLES", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.SDE_ID);

            e.Property(x => x.SDE_IVA).HasPrecision(9, 2);
            e.Property(x => x.SDE_PRIMER_PAGO).HasPrecision(9, 2);
            e.Property(x => x.SDE_PAGO_SUBSECUENTE).HasPrecision(9, 2);
            e.Property(x => x.SDE_PRIMA_NETA).HasPrecision(9, 2);
            e.Property(x => x.SDE_RECARGO_FRACCIONADO).HasPrecision(9, 2);
            e.Property(x => x.SDE_DERECHO_EXPEDICION).HasPrecision(9, 2);
            e.Property(x => x.SDE_PRIMA_TOTAL).HasPrecision(9, 2);
            e.Property(x => x.SDE_UDI).HasPrecision(5, 2);
            e.Property(x => x.SDE_DEDUCIBLE_DANIOS).HasPrecision(5, 2);
            e.Property(x => x.SDE_DEDUCIBLE_ROBO_TOTAL).HasPrecision(5, 2);
            e.Property(x => x.SDE_DEDUCIBLE_ROBO_PARCIAL).HasPrecision(5, 2);
            e.Property(x => x.SDE_RC_LUC).HasPrecision(10, 2);
            e.Property(x => x.SDE_ACC_CONDUCTOR).HasPrecision(10, 2);
            e.Property(x => x.SDE_GM_OCUPANTES).HasPrecision(10, 2);
            e.Property(x => x.SDE_MONTO_UDI).HasPrecision(9, 2);
            e.Property(x => x.SDE_DEDUCIBLE_RC).HasPrecision(5, 2);
            e.Property(x => x.SDE_VALOR_ADAPTACION).HasPrecision(12, 2);
            e.Property(x => x.SDE_MONTO_DEDUCIBLE).HasPrecision(10, 2);
            e.Property(x => x.SDE_VALOR_FACTURA).HasPrecision(10, 2);
            e.Property(x => x.SDE_VALOR_COMERCIAL).HasPrecision(10, 2);
        });

        mb.Entity<ArchivosRepositoriosModel>(e =>
        {
            e.ToTable("ARCHIVOS_REPOSITORIOS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.ARC_ID);
            // ARC_ID no es IDENTITY: se genera manualmente vía SP_ACTUALIZAR_SECUENCIAS.
            e.Property(x => x.ARC_ID).ValueGeneratedNever();
        });

        mb.Entity<DocumentosUnidadesModel>(e =>
        {
            e.ToTable("DOCUMENTOS_UNIDADES", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.DUN_ID);
        });

        mb.Entity<ComprasDetallesModel>(e =>
        {
            e.ToTable("COMPRAS_DETALLES");
            e.HasKey(x => x.CDE_ID);
        });

        mb.Entity<ComprasModel>(e =>
        {
            e.ToTable("COMPRAS");
            e.HasKey(x => x.COM_ID);
        });

        mb.Entity<VehiculosModel>(e =>
        {
            e.ToTable("VEHICULOS");
            e.HasKey(x => x.VHC_ID);
        });

        mb.Entity<AseguradorasModel>(e =>
        {
            e.ToTable("ASEGURADORAS");
            e.HasKey(x => x.ASE_ID);
        });

        mb.Entity<TiposFormasPagosSegurosModel>(e =>
        {
            e.ToTable("TIPOS_FORMAS_PAGOS_SEGUROS");
            e.HasKey(x => x.TFS_ID);
        });

        mb.Entity<BeneficiariosPreferentesModel>(e =>
        {
            e.ToTable("BENEFICIARIOS_PREFERENTES");
            e.HasKey(x => x.BPR_ID);
        });

        mb.Entity<EmpresasModel>(e =>
        {
            e.ToTable("EMPRESAS");
            e.HasKey(x => x.EMP_ID);
        });

        mb.Entity<TiposAdministracionesCarteraModel>(e =>
        {
            e.ToTable("TIPOS_ADMINISTRACIONES_CARTERA");
            e.HasKey(x => x.TTC_ID);
        });

        mb.Entity<TiposUsosModel>(e =>
        {
            e.ToTable("TIPOS_USOS");
            e.HasKey(x => x.TUS_ID);
        });

        mb.Entity<TiposNoPasajerosModel>(e =>
        {
            e.ToTable("TIPOS_NO_PASAJEROS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.TNS_ID);
        });

        mb.Entity<TiposPolizasModel>(e =>
        {
            e.ToTable("TIPOS_POLIZAS");
            e.HasKey(x => x.TPZ_ID);
        });

        mb.Entity<TiposCoberturasModel>(e =>
        {
            e.ToTable("TIPOS_COBERTURAS");
            e.HasKey(x => x.TCX_ID);
        });

        mb.Entity<TiposGestionPagosSeguroModel>(e =>
        {
            e.ToTable("TIPOS_GESTION_PAGOS_SEGURO");
            e.HasKey(x => x.TGS_ID);
        });

        mb.Entity<TiposValoresSeguroModel>(e =>
        {
            e.ToTable("TIPOS_VALORES_SEGURO");
            e.HasKey(x => x.TVS_ID);
        });

        mb.Entity<UsuariosModel>(e =>
        {
            e.ToTable("USUARIOS");
            e.HasKey(x => x.USU_ID);
        });

        mb.Entity<SiniestrosModel>(e =>
        {
            e.ToTable("SINIESTROS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.SIN_ID);

            e.Property(x => x.SIN_MONTO_INDEMNIZABLE).HasPrecision(9, 2);
            e.Property(x => x.SIN_MONTO_DEDUCIBLE).HasPrecision(9, 2);
            e.Property(x => x.SIN_MONTO_PRIMAS_PENDIENTES).HasPrecision(9, 2);
            e.Property(x => x.SIN_MONTO_OTROS_DESCUENTOS).HasPrecision(9, 2);
        });

        mb.Entity<SiniestrosEstatusModel>(e =>
        {
            e.ToTable("SINIESTROS_ESTATUS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.SES_ID);
        });

        mb.Entity<DocumentosSiniestrosModel>(e =>
        {
            e.ToTable("DOCUMENTOS_SINIESTROS", tb => tb.UseSqlOutputClause(false));
            e.HasKey(x => x.DSI_ID);
        });

        mb.Entity<TiposSiniestrosModel>(e =>
        {
            e.ToTable("TIPOS_SINIESTROS");
            e.HasKey(x => x.TSI_ID);
        });

        mb.Entity<TiposOrigenesModel>(e =>
        {
            e.ToTable("TIPOS_ORIGENES");
            e.HasKey(x => x.TOR_ID);
        });

        mb.Entity<TiposEstatusModel>(e =>
        {
            e.ToTable("TIPOS_ESTATUS");
            e.HasKey(x => x.TES_ID);
        });

        mb.Entity<LogErroresModel>(e =>
        {
            e.ToTable("LOG_ERRORES");
            e.HasKey(x => x.LER_ID);
            e.Property(x => x.LER_ORIGEN).HasMaxLength(250);
            e.Property(x => x.LER_MENSAJE_ERROR).HasMaxLength(4000);
        });
    }
}
