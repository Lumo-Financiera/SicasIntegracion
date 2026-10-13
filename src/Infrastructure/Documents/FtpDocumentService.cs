using FluentFTP;
using FluentFTP.Exceptions;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LumoSys.Integraciones.Infrastructure.Documents;

public sealed class FtpOptions
{
    public string Host { get; init; } = string.Empty;
    public string Usuario { get; init; } = string.Empty;
    public string Contrasena { get; init; } = string.Empty;
    public string RutaDestinoPolizas { get; init; } = "Fleet/Documentos Unidades/2/0/";
    public string RutaDestinoSiniestros { get; init; } = "Seguros/Siniestros/";
}

public sealed class FtpDocumentService(
    IOptions<FtpOptions> opts,
    IMonitoreoErrores monitoreo,
    ILogger<FtpDocumentService> log) : IDocumentService
{
    private readonly FtpOptions _opts = opts.Value;

    public async Task<bool> SubirDocumentoPoliza(
        string nombreArchivoRemoto, byte[] bytes, CancellationToken ct = default)
    {
        int status = await SubirArchivo(_opts.RutaDestinoPolizas, nombreArchivoRemoto, bytes, ct);
        return status > 0;
    }

    public async Task<string?> SubirDocumentoSiniestro(
        string nombreArchivo, byte[] bytes, CancellationToken ct = default)
    {
        string nombreFinal = $"{Path.GetFileNameWithoutExtension(nombreArchivo)}_3_{DateTime.Now:ddMMyyHHmmss}{Path.GetExtension(nombreArchivo)}";
        int status = await SubirArchivo(_opts.RutaDestinoSiniestros, nombreFinal, bytes, ct);
        return status > 0 ? nombreFinal : null;
    }

    private async Task<int> SubirArchivo(string rutaDestino, string nombreArchivo, byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var ftp = new AsyncFtpClient(_opts.Host, _opts.Usuario, _opts.Contrasena);
            ftp.Config.EncryptionMode = FtpEncryptionMode.None;

            await ftp.Connect(ct);
            await ftp.SetWorkingDirectory(rutaDestino, ct);

            using var ms = new MemoryStream(bytes);
            var status = await ftp.UploadStream(ms, nombreArchivo, FtpRemoteExists.Skip, token: ct);

            if (status == FtpStatus.Success || status == FtpStatus.Skipped)
            {
                log.LogInformation("FTP: {Archivo} subido a {Ruta}", nombreArchivo, rutaDestino);
                monitoreo.Rastrear("ftp.subida", $"{nombreArchivo} subido a {rutaDestino}",
                    ("estado", status.ToString()), ("bytes", bytes.Length.ToString()));
                return 1;
            }

            // LogError y no LogWarning: el resultado para el negocio es idéntico al de una
            // excepción —el documento no quedó subido— y sin esto el caso no generaba ningún
            // evento en Sentry, solo un rastro que nadie llegaría a ver.
            log.LogError("FTP: {Archivo} no se subió a {Ruta} (estado {Status})",
                nombreArchivo, rutaDestino, status);
            monitoreo.RastrearFallo("ftp.subida", $"{nombreArchivo} no se subió",
                ("estado", status.ToString()), ("ruta", rutaDestino));
            return 0;
        }
        catch (FtpAuthenticationException ex)
        {
            // El servidor rechazó el login. Es el fallo que más tiempo ha costado diagnosticar en
            // este proyecto, y siempre por lo mismo: el evento decía "error de FTP" sin el código
            // ni el texto del servidor, que es justo lo que distingue una causa de otra.
            //
            // Quien opera este servicio NO administra las cuentas del FTP: tiene que reenviarle el
            // evento a quien sí las administra. Por eso el evento lleva el código crudo, la
            // respuesta literal y una lectura en español de lo que hay que pedir.
            ReportarRechazoDeLogin(ex.CompletionCode, ex.Message, rutaDestino, nombreArchivo, ex);
            return 0;
        }
        catch (FtpCommandException ex)
        {
            // Login correcto pero el comando falló: típicamente 550 (ruta inexistente o sin
            // permiso de escritura sobre ella). Se separa del caso anterior porque lo que hay que
            // pedirle al administrador es distinto.
            monitoreo.Capturar(ex, "ftp.subir-archivo",
                ("ftp_host", _opts.Host),
                ("ftp_ruta", rutaDestino),
                ("ftp_usuario", _opts.Usuario),
                ("ftp_codigo", ex.CompletionCode),
                ("ftp_respuesta", ex.Message),
                ("archivo", nombreArchivo),
                ("diagnostico", DiagnosticarFtp(ex.CompletionCode, ex.Message, rutaDestino)),
                ("consecuencia", "documento-no-subido"));
            monitoreo.AgregarDato("archivo_bytes", bytes.Length);

            log.LogError(ex, "FTP rechazó el comando ({Codigo}) subiendo {Archivo} a {Ruta}",
                ex.CompletionCode, nombreArchivo, rutaDestino);
            return 0;
        }
        catch (Exception ex)
        {
            // Se conserva el retorno 0 (contrato de IDocumentService: el llamador decide si el
            // documento se reintenta o se omite), pero el fallo se reporta con el destino real —
            // sin el host y la ruta, un timeout de red no se distingue de un archivo corrupto.
            monitoreo.Capturar(ex, "ftp.subir-archivo",
                ("ftp_host", _opts.Host),
                ("ftp_ruta", rutaDestino),
                ("ftp_usuario", _opts.Usuario),
                ("archivo", nombreArchivo),
                ("diagnostico", "No hubo respuesta del servidor FTP: revisar red, firewall o que el servicio FTP esté arriba."),
                ("consecuencia", "documento-no-subido"));
            monitoreo.AgregarDato("archivo_bytes", bytes.Length);

            log.LogError(ex, "Error FTP subiendo {Archivo}", nombreArchivo);
            return 0;
        }
    }

    /// <summary>
    /// Reporta el rechazo de login como fallo CRÍTICO: si el FTP no deja entrar, no se almacena
    /// ningún documento de ninguna póliza ni siniestro, no es el problema de un archivo.
    /// </summary>
    private void ReportarRechazoDeLogin(
        string codigo, string respuesta, string rutaDestino, string nombreArchivo, Exception ex)
    {
        string diagnostico = DiagnosticarFtp(codigo, respuesta, rutaDestino);

        monitoreo.ReportarFalloCritico("ftp.login",
            $"El FTP rechazó el acceso: {codigo} {respuesta}",
            "ningun-documento-se-puede-almacenar",
            ("ftp_host", _opts.Host),
            ("ftp_usuario", _opts.Usuario),
            ("ftp_ruta", rutaDestino),
            ("ftp_codigo", codigo),
            ("ftp_respuesta", respuesta),
            ("diagnostico", diagnostico),
            ("archivo", nombreArchivo));

        monitoreo.Capturar(ex, "ftp.login",
            ("ftp_codigo", codigo),
            ("diagnostico", diagnostico));

        log.LogError(ex, "FTP rechazó el login de {Usuario} en {Host} ({Codigo} {Respuesta}). {Diagnostico}",
            _opts.Usuario, _opts.Host, codigo, respuesta, diagnostico);
    }

    /// <summary>
    /// Traduce la respuesta del servidor FTP a la causa concreta y a lo que hay que pedirle al
    /// administrador del FTP.
    ///
    /// Existe porque quien opera este integrador no administra esas cuentas: el valor de esta
    /// cadena es que se pueda copiar del evento de Sentry y pegar en la solicitud, sin que nadie
    /// tenga que interpretar un código FTP. Los textos salen de lo observado en este servidor
    /// (IIS FTP con autenticación de paso a través) — ver la sección de FTP en CLAUDE.md.
    /// </summary>
    private string DiagnosticarFtp(string codigo, string respuesta, string rutaDestino)
    {
        string texto = respuesta?.ToLowerInvariant() ?? string.Empty;

        if (codigo == "530" && texto.Contains("home directory"))
            return $"La cuenta '{_opts.Usuario}' se autentica pero IIS no puede abrir su directorio raíz. " +
                   "PEDIR AL ADMINISTRADOR DEL FTP: (1) dar permisos NTFS de Modificación a esa cuenta sobre la " +
                   "ruta física del sitio FTP, y (2) revisar el Aislamiento de usuarios del sitio — si está en " +
                   "'Directorio con nombre de usuario', hace falta la carpeta LocalUser\\<usuario> o cambiarlo a " +
                   "'No aislar usuarios'. NO es un problema de contraseña.";

        if (codigo == "530" && (texto.Contains("password") || texto.Contains("contrase")))
            return $"Contraseña rechazada o caducada para '{_opts.Usuario}'. PEDIR AL ADMINISTRADOR DEL FTP: " +
                   "confirmar la contraseña vigente y desmarcar 'El usuario debe cambiar la contraseña en el " +
                   "siguiente inicio de sesión'.";

        if (codigo == "530")
            return $"La cuenta '{_opts.Usuario}' no puede iniciar sesión. PEDIR AL ADMINISTRADOR DEL FTP: " +
                   "confirmar que la cuenta existe, está activa y no bloqueada; que la autenticación básica está " +
                   "habilitada en el sitio; y que la cuenta figura en las reglas de autorización con Lectura y Escritura.";

        if (codigo == "550")
            return $"El servidor no aceptó la operación sobre '{rutaDestino}'. PEDIR AL ADMINISTRADOR DEL FTP: " +
                   $"confirmar que esa carpeta existe y que '{_opts.Usuario}' tiene permiso de ESCRITURA sobre ella.";

        if (codigo == "553")
            return $"Nombre de archivo no permitido en '{rutaDestino}'. Suele ser una regla del servidor o falta de permiso de escritura.";

        if (codigo.StartsWith("4"))
            return "Fallo temporal del servidor FTP (buzón/disco ocupado o sin espacio). Reintentar; si persiste, " +
                   "PEDIR AL ADMINISTRADOR DEL FTP revisar espacio en disco y estado del servicio.";

        return $"Respuesta no catalogada del servidor FTP ({codigo}). Reenviar este evento completo al administrador del FTP.";
    }
}
