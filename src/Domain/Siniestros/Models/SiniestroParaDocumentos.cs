namespace LumoSys.Integraciones.Domain.Siniestros.Models;

/// <summary>
/// Siniestro al que hay que volver a revisarle el Centro Digital de SICAS.
///
/// Lo mínimo para preguntar por sus archivos: el id en LumoSys, el folio para los mensajes, y el
/// <c>SIN_FOLIO_SICAS</c>, que ES el IDSiniestro que <c>GetFiles</c> espera como <c>ValuePK</c> —
/// por eso esta fase no necesita localizar antes el siniestro en SICAS, a diferencia de la
/// reconciliación de estatus.
/// </summary>
/// <param name="SiniestroId">SIN_ID en dbLumoSys.</param>
/// <param name="NoReporte">Folio con el que operaciones lo identifica.</param>
/// <param name="FolioSicas">SIN_FOLIO_SICAS = IDSiniestro de SICAS.</param>
public sealed record SiniestroParaDocumentos(
    int SiniestroId,
    string NoReporte,
    int FolioSicas);
