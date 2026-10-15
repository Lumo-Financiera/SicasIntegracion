namespace LumoSys.Integraciones.Domain.Siniestros.Models;

/// <summary>
/// Siniestro que LumoSys muestra abierto. Lo consume la fase de reconciliación, que vuelve a
/// preguntarle su estatus a SICAS.
///
/// Hace falta porque el barrido diario filtra por <c>DatSiniestros.FCaptura</c>, la fecha de ALTA
/// del siniestro: uno capturado hace meses y cerrado ayer no vuelve a aparecer nunca en el rango
/// "ayer → hoy", así que su estatus se queda congelado en LumoSys aunque SICAS ya lo haya cerrado.
/// </summary>
/// <param name="SiniestroId">SIN_ID en dbLumoSys.</param>
/// <param name="NoReporte">Folio con el que operaciones lo identifica.</param>
/// <param name="FolioSicas">SIN_FOLIO_SICAS (IDSiniestro de SICAS); null si el registro se capturó
/// a mano y nunca estuvo vinculado a SICAS.</param>
/// <param name="EstatusActual">Lo que LumoSys muestra hoy. Viene en la misma consulta para que la
/// reconciliación pueda comparar contra SICAS sin pedir nada más: si ya coinciden no hay que tocar
/// el siniestro ni traer su bitácora, y eso ahorra una consulta por cada siniestro que está bien,
/// que son la mayoría.</param>
/// <param name="UltimaActualizacion">Fecha del comentario vigente, solo para dejar constancia de
/// cuánto llevaba sin moverse un siniestro cuando se cierra.</param>
public sealed record SiniestroAbierto(
    int SiniestroId,
    string NoReporte,
    int? FolioSicas,
    string EstatusActual,
    DateTime UltimaActualizacion);
