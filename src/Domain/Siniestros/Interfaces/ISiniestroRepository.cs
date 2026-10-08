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

    Task<bool> ExisteDocumentoAsync(int siniestroId, string nombreBase, CancellationToken ct = default);
    Task RegistrarDocumentoAsync(int siniestroId, string nombreArchivo, CancellationToken ct = default);
    Task<int?> BuscarIdPorReporte(string noReporte, CancellationToken ct = default);

    /// <summary>Busca por SIN_FOLIO_SICAS (IDSiniestro interno de SICAS) — es el único campo que
    /// trae la bitácora H03314011 para vincular cada comentario a su siniestro (NumReporte/folio
    /// no viene en esa respuesta).</summary>
    Task<int?> BuscarIdPorFolioSicas(int idSiniestroSicas, CancellationToken ct = default);
}
