namespace LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;

public sealed class SubirDocumentoPolizaCommand
{
    public string Serie { get; init; } = string.Empty;
    public string NombreArchivo { get; init; } = string.Empty;
    public byte[] Bytes { get; init; } = [];
}
