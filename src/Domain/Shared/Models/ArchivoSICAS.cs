namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

/// <summary>
/// Un archivo del Centro Digital de SICAS.
///
/// El nombre sale de <see cref="FileName"/> + <see cref="Ext"/>, que es lo que SICAS entrega como
/// campos propios. Antes se deducía partiendo <see cref="PathWWW"/> por "/", y eso se rompe en
/// cuanto PathWWW deja de ser una URL: con <c>URLSecurity=1</c> ese campo trae el contenido del
/// archivo en base64, así que el "nombre" habría sido un trozo del base64. Se conserva el parseo
/// de la URL solo como respaldo, por si alguna respuesta no trae FileName.
/// </summary>
public sealed class ArchivoSICAS
{
    /// <summary>Nombre sin extensión, tal como lo da SICAS.</summary>
    public string? FileName { get; init; }

    /// <summary>Extensión sin punto ("pdf", "jpg").</summary>
    public string? Ext { get; init; }

    /// <summary>
    /// Con <c>URLSecurity=0</c> es la URL de descarga directa (host <c>www.sicasonline.info</c>).
    /// Con <c>URLSecurity=1</c> es el contenido del archivo en base64.
    /// </summary>
    public string? PathWWW { get; init; }

    /// <summary>Tamaño que reporta SICAS, en texto ("15.1 MB"). Solo informativo.</summary>
    public string? SizeFile { get; init; }

    public string NombreArchivo
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FileName))
                return string.IsNullOrWhiteSpace(Ext) ? FileName : $"{FileName}.{Ext}";

            return PathWWW?.Split('/').LastOrDefault() ?? string.Empty;
        }
    }

    /// <summary>true si <see cref="PathWWW"/> trae una URL descargable y no el contenido en base64.</summary>
    public bool TieneUrlDescargable =>
        PathWWW is not null &&
        (PathWWW.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         PathWWW.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
