namespace LumoSys.Integraciones.Infrastructure.Persistence.Models;

// ─── Catálogos de Seguros ───────────────────────────────────────────────────────

public sealed class AseguradorasModel
{
    public byte ASE_ID { get; set; }
    public string? ASE_DESCRIPCION { get; set; }
}

public sealed class TiposFormasPagosSegurosModel
{
    public byte TFS_ID { get; set; }
    public string? TFS_DESCRIPCION { get; set; }
}

public sealed class BeneficiariosPreferentesModel
{
    public byte BPR_ID { get; set; }
    public string? BPR_DESCRIPCION { get; set; }
}

public sealed class EmpresasModel
{
    public byte EMP_ID { get; set; }
    public string? EMP_DESCRIPCION { get; set; }
    public string? EMP_RAZON_SOCIAL { get; set; }
    public string? EMP_NOMBRE_CORTO { get; set; }
}

public sealed class TiposAdministracionesCarteraModel
{
    public byte TTC_ID { get; set; }
    public string? TTC_DESCRIPCION { get; set; }
}

public sealed class TiposUsosModel
{
    public byte TUS_ID { get; set; }
    public string? TUS_DESCRIPCION { get; set; }
}

public sealed class TiposNoPasajerosModel
{
    public byte TNS_ID { get; set; }
    public string? TNS_DESCRIPCION { get; set; }
}

public sealed class TiposPolizasModel
{
    public byte TPZ_ID { get; set; }
    public string? TPZ_DESCRIPCION { get; set; }
}

public sealed class TiposCoberturasModel
{
    public byte TCX_ID { get; set; }
    public string? TCX_DESCRIPCION { get; set; }
}

public sealed class TiposGestionPagosSeguroModel
{
    public byte TGS_ID { get; set; }
    public string? TGS_DESCRIPCION { get; set; }
}

public sealed class TiposValoresSeguroModel
{
    public byte TVS_ID { get; set; }
    public string? TVS_DESCRIPCION { get; set; }
}

public sealed class UsuariosModel
{
    public int USU_ID { get; set; }
    public string USU_NOMBRE { get; set; } = string.Empty;
    public string USU_APELLIDO_PATERNO { get; set; } = string.Empty;
    public string USU_APELLIDO_MATERNO { get; set; } = string.Empty;
}

// ─── Seguros ──────────────────────────────────────────────────────────────────

public sealed class SegurosModel
{
    public int SEG_ID { get; set; }
    public byte SEG_ASE_ID { get; set; }
    public string SEG_NO_POLIZA { get; set; } = string.Empty;
    public byte? SEG_BPR_ID { get; set; }
    public DateTime SEG_FECHA_REGISTRO { get; set; }
    public int SEG_USU_ID { get; set; }
    public bool SEG_ACTIVO { get; set; }
    public string? SEG_BROKER { get; set; }
    public byte? SEG_TFS_ID { get; set; }
    public string? SEG_BENEFICIARIO_PREFERENTE { get; set; }
    public byte? SEG_EMP_ID { get; set; }
}

public sealed class SegurosDetallesModel
{
    public int SDE_ID { get; set; }
    public int SDE_SEG_ID { get; set; }
    public int? SDE_CDE_ID { get; set; }
    public int? SDE_INCISO { get; set; }
    public decimal? SDE_IVA { get; set; }
    public decimal? SDE_PRIMER_PAGO { get; set; }
    public decimal? SDE_PAGO_SUBSECUENTE { get; set; }
    public decimal? SDE_PRIMA_NETA { get; set; }
    public decimal? SDE_RECARGO_FRACCIONADO { get; set; }
    public decimal? SDE_DERECHO_EXPEDICION { get; set; }
    public DateTime? SDE_FECHA_CONTRATACION { get; set; }
    public DateTime? SDE_FECHA_VENCIMIENTO { get; set; }
    public decimal? SDE_PRIMA_TOTAL { get; set; }
    public int? SDE_TES_ID { get; set; }
    public decimal? SDE_UDI { get; set; }
    public decimal? SDE_DEDUCIBLE_DANIOS { get; set; }
    public decimal? SDE_DEDUCIBLE_ROBO_TOTAL { get; set; }
    public decimal? SDE_DEDUCIBLE_ROBO_PARCIAL { get; set; }
    public decimal? SDE_RC_LUC { get; set; }
    public decimal? SDE_ACC_CONDUCTOR { get; set; }
    public decimal? SDE_GM_OCUPANTES { get; set; }
    public string? SDE_COMENTARIOS_CANCELACION { get; set; }
    public DateTime? SDE_FECHA_CANCELACION { get; set; }
    public byte? SDE_TTC_ID { get; set; }
    public string? SDE_OBSERVACIONES_ADMON_CARTERA { get; set; }
    public byte? SDE_TUS_ID { get; set; }
    public byte? SDE_TNS_ID { get; set; }
    public byte? SDE_TPZ_ID { get; set; }
    public byte? SDE_TCX_ID { get; set; }
    public int? SDE_VHC_ID { get; set; }
    public string? SDE_DESCRIPCION_ADAPTACION { get; set; }
    public int? SDE_USU_ID_EJECUTIVO { get; set; }
    public decimal? SDE_MONTO_UDI { get; set; }
    public byte? SDE_TGS_ID { get; set; }
    public byte? SDE_TVS_ID_DANIOS { get; set; }
    public byte? SDE_TVS_ID_ROBO_TOTAL { get; set; }
    public bool? SDE_AMPARO_ROBO_PARCIAL { get; set; }
    public bool? SDE_AMPARO_ADAPTACIONES { get; set; }
    public bool? SDE_AMPARO_RC_CRUZADA { get; set; }
    public bool? SDE_AMPARO_RC_PASAJEROS { get; set; }
    public bool? SDE_AMPARO_RC_EXTRANJERO { get; set; }
    public string? SDE_AMPARO_RC_ADICIONALES { get; set; }
    public decimal? SDE_DEDUCIBLE_RC { get; set; }
    public string? SDE_OTROS { get; set; }
    public decimal? SDE_VALOR_ADAPTACION { get; set; }
    public bool? SDE_AMPARO_JURIDICO { get; set; }
    public bool? SDE_AMPARO_VIAL { get; set; }
    public decimal? SDE_MONTO_DEDUCIBLE { get; set; }
    public decimal? SDE_VALOR_FACTURA { get; set; }
    public decimal? SDE_VALOR_COMERCIAL { get; set; }
}

public sealed class ArchivosRepositoriosModel
{
    public int ARC_ID { get; set; }
    public int ARC_REP_ID { get; set; }
    public string ARC_NOMBRE_ARCHIVO { get; set; } = string.Empty;
    public long ARC_NO_BYTES { get; set; }
    public int ARC_TMM_ID { get; set; }
    public int ARC_USU_ID { get; set; }
    public DateTime ARC_FECHA_REGISTRO { get; set; }
}

public sealed class DocumentosUnidadesModel
{
    public int DUN_ID { get; set; }
    public int DUN_CDE_ID { get; set; }
    public int DUN_ARC_ID { get; set; }
    public int DUN_CLI_ID { get; set; }
    public int? DUN_TDW_ID { get; set; }
    public int? DUN_MDE_ID { get; set; }
    public int DUN_USU_ID { get; set; }
    public bool DUN_PUBLICO { get; set; }
    public DateTime DUN_FECHA_REGISTRO { get; set; }
}

public sealed class ComprasDetallesModel
{
    public int CDE_ID { get; set; }
    public int? CDE_COM_ID { get; set; }
    public string? CDE_NO_SERIE { get; set; }
    public bool CDE_COMPLETO { get; set; }
}

public sealed class ComprasModel
{
    public int COM_ID { get; set; }
    public int? COM_CLI_ID { get; set; }
    public int? COM_TES_ID { get; set; }
}

public sealed class VehiculosModel
{
    public int VHC_ID { get; set; }
    public string? VHC_NO_SERIE { get; set; }
    public int? VHC_CLI_ID { get; set; }
}

// ─── Siniestros ───────────────────────────────────────────────────────────────

public sealed class SiniestrosModel
{
    public int SIN_ID { get; set; }
    public int SIN_CDE_ID { get; set; }
    public int? SIN_SEG_ID { get; set; }
    public string? SIN_NO_REPORTE { get; set; }
    public string? SIN_NO_SINIESTRO { get; set; }
    public int SIN_TSI_ID { get; set; }
    public DateTime? SIN_FECHA_EVENTO { get; set; }
    public DateTime? SIN_FECHA_RESOLUCION { get; set; }
    public string? SIN_DESCRIPCION { get; set; }
    public decimal? SIN_MONTO_INDEMNIZABLE { get; set; }
    public decimal? SIN_MONTO_DEDUCIBLE { get; set; }
    public decimal? SIN_MONTO_PRIMAS_PENDIENTES { get; set; }
    public byte? SIN_TOR_ID { get; set; }
    public DateTime SIN_FECHA_REGISTRO { get; set; }
    public int SIN_USU_ID { get; set; }
    public int? SIN_VRE_ID { get; set; }
    public decimal? SIN_MONTO_OTROS_DESCUENTOS { get; set; }
    public bool? SIN_PAGO_DEDUCIBLE_CLIENTE { get; set; }
    public int? SIN_FOLIO_SFLEET { get; set; }
    public int? SIN_FOLIO_SICAS { get; set; }
}

public sealed class SiniestrosEstatusModel
{
    public int SES_ID { get; set; }
    public int SES_SIN_ID { get; set; }
    public int SES_TES_ID { get; set; }
    public string? SES_COMENTARIOS { get; set; }
    public DateTime SES_FECHA_REGISTRO { get; set; }
    public int SES_USU_ID { get; set; }
}

public sealed class DocumentosSiniestrosModel
{
    public int DSI_ID { get; set; }
    public int DSI_SIN_ID { get; set; }
    public string DSI_ARCHIVO { get; set; } = string.Empty;
    public DateTime DSI_FECHA_REGISTRO { get; set; }
    public int DSI_USU_ID { get; set; }
}

public sealed class TiposSiniestrosModel
{
    public int TSI_ID { get; set; }
    public string TSI_DESCRIPCION { get; set; } = string.Empty;
}

public sealed class TiposOrigenesModel
{
    public byte TOR_ID { get; set; }
    public string TOR_DESCRIPCION { get; set; } = string.Empty;
}

public sealed class TiposEstatusModel
{
    public int TES_ID { get; set; }
    public string TES_DESCRIPCION { get; set; } = string.Empty;
    public byte TES_TMO_ID { get; set; }
}

// ─── Log de errores (compartido con el resto de LumoSys) ───────────────────────

public sealed class LogErroresModel
{
    public int LER_ID { get; set; }
    public string LER_ORIGEN { get; set; } = string.Empty;
    public int LER_NO_ERROR { get; set; }
    public string LER_MENSAJE_ERROR { get; set; } = string.Empty;
    public byte LER_TLG_ID { get; set; }
    public bool LER_REVISADO { get; set; }
    public DateTime LER_FECHA_REGISTRO { get; set; }
}
