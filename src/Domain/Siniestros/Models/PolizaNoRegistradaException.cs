namespace LumoSys.Integraciones.Domain.Siniestros.Models;

/// <summary>
/// Un siniestro no se puede guardar porque su póliza todavía no está en SEGUROS.
///
/// Se distingue del resto de errores para poder reaccionar: el siniestro no tiene nada malo, solo
/// llegó antes que su póliza. El handler trae la póliza de SICAS y reintenta una vez, en lugar de
/// descartar el siniestro. Sin esto quedaban bloqueados indefinidamente — 042619730 (póliza
/// 5267524) y 441837 (L0000006490-0) estuvieron así semanas.
/// </summary>
public sealed class PolizaNoRegistradaException(string numeroPoliza)
    : InvalidOperationException($"La Póliza '{numeroPoliza}' no se encuentra registrada en SEGUROS.")
{
    public string NumeroPoliza { get; } = numeroPoliza;
}
