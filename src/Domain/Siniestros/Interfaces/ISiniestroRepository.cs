using LumoSys.Integraciones.Domain.Siniestros.Models;

namespace LumoSys.Integraciones.Domain.Siniestros.Interfaces;

public interface ISiniestroRepository
{
    /// <summary>true si la serie existe en COMPRAS_DETALLES (con estatus válido) — pertenece a la
    /// flotilla propia. SIN_CDE_ID es NOT NULL y no tiene equivalente a VEHICULOS (a diferencia de
    /// Seguros), así que un vehículo "externo" no puede tener siniestro registrado.</summary>
    Task<bool> ExisteVehiculoAsync(string serie, CancellationToken ct = default);

    Task<int> UpsertSiniestroAsync(DatosSiniestro datos, CancellationToken ct = default);
    Task<bool> ExisteEstatusAsync(int siniestroId, string comentarios, DateTime fechaRegistro, CancellationToken ct = default);
    Task UpsertEstatusAsync(DatosEstatus datos, int siniestroId, CancellationToken ct = default);

    /// <summary>Corrige el estatus de un comentario que YA existe, cuando no concuerda con el que
    /// le corresponde. Devuelve cuántas filas cambió. No toca el texto, la fecha ni el usuario.</summary>
    Task<int> CorregirTipoEstatusAsync(int siniestroId, string comentarios, DateTime fechaRegistro,
        string estatusCorrecto, CancellationToken ct = default);

    /// <summary>Deja el estatus que LumoSys MUESTRA (el del comentario de fecha/hora más reciente)
    /// igual al que reporta SICAS. Devuelve true si tuvo que cambiarlo.</summary>
    Task<bool> AlinearEstatusEfectivoAsync(int siniestroId, string estatusOficial,
        CancellationToken ct = default);

    /// <summary>Siniestros que LumoSys muestra abiertos, para la fase de reconciliación.</summary>
    Task<List<SiniestroAbierto>> ObtenerSiniestrosAbiertosAsync(CancellationToken ct = default);

    /// <summary>
    /// Siniestros a los que hay que volver a preguntarles si SICAS publicó documentos nuevos.
    ///
    /// Es la UNIÓN de dos conjuntos, y los dos hacen falta:
    /// los que siguen abiertos (sin importar su antigüedad) y los registrados en los últimos
    /// <paramref name="diasAtras"/> días **aunque ya estén cerrados**. Lo segundo no es un extra:
    /// medido el 13/10/2026, de los 225 siniestros con documentos pendientes en SICAS, 159 estaban
    /// cerrados — el ajustador sube el expediente después de que se resuelve el caso.
    /// Solo devuelve los que tienen SIN_FOLIO_SICAS, que es el IDSiniestro con el que se consulta
    /// el Centro Digital.
    /// </summary>
    /// <param name="completa">true devuelve TODOS los siniestros vinculados a SICAS, ignorando la
    /// ventana y el estatus: es la puesta al día que corre al desplegar. false aplica el filtro
    /// incremental (sin documentos ∪ abiertos ∪ recientes), que se reduce solo conforme se migran.</param>
    Task<List<SiniestroParaDocumentos>> ObtenerParaRevisionDocumentalAsync(
        int diasAtras, bool completa, CancellationToken ct = default);

    Task<bool> ExisteDocumentoAsync(int siniestroId, string nombreBase, CancellationToken ct = default);
    Task RegistrarDocumentoAsync(int siniestroId, string nombreArchivo, CancellationToken ct = default);
    Task<int?> BuscarIdPorReporte(string noReporte, CancellationToken ct = default);

    /// <summary>Busca por SIN_FOLIO_SICAS (IDSiniestro interno de SICAS) — es el único campo que
    /// trae la bitácora H03314011 para vincular cada comentario a su siniestro (NumReporte/folio
    /// no viene en esa respuesta).</summary>
    Task<int?> BuscarIdPorFolioSicas(int idSiniestroSicas, CancellationToken ct = default);
}
