namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public sealed class ArchivoSICAS
{
    public string? PathWWW { get; init; }
    public string NombreArchivo => PathWWW?.Split('/').LastOrDefault() ?? string.Empty;
}
