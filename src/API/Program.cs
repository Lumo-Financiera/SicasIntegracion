using System.Reflection;
using LumoSys.Integraciones.API.BackgroundServices;
using LumoSys.Integraciones.API.Middlewares;
using LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;
using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using Microsoft.Extensions.Options;
using LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;
using LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;
using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Infrastructure.Extensions;
using LumoSys.Integraciones.Infrastructure.Notifications;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Permite correr como servicio de Windows en producción (sc.exe create ...).
// Sin efecto cuando se ejecuta interactivo (dotnet run / consola).
builder.Host.UseWindowsService(opts => opts.ServiceName = "LumoSysIntegraciones");

// appsettings.Local.json: credenciales reales para pruebas locales, nunca se versiona.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var cfg = builder.Configuration;

// ── Log de archivo ─────────────────────────────────────────────────────────
// Va primero y aparte: corriendo como servicio de Windows no hay consola, y UseWindowsService()
// solo manda al Event Log desde Warning. Sin este proveedor, todo el ILogger del ETL se pierde.
builder.Services.AddLogArchivo(cfg);

// Sin tracking de Activity: en un ETL, SpanId/TraceId/ParentId solo alargan cada línea del log.
// La correlación útil la da el scope propio de cada lote (ver ResumenLote.CorrelacionId).
builder.Logging.Configure(opts => opts.ActivityTrackingOptions = ActivityTrackingOptions.None);

// ── Application handlers ───────────────────────────────────────────────────
builder.Services.AddScoped<GuardarPolizaHandler>();
builder.Services.AddScoped<SubirDocumentoPolizaHandler>();
builder.Services.AddScoped<ProcesarLoteSeguroHandler>();
builder.Services.AddScoped<GuardarSiniestroHandler>();
builder.Services.AddScoped<ProcesarLoteSiniestroHandler>();

// ── Infrastructure (EF Core, SICAS, SFleet, FTP, Repositorios) ─────────────
builder.Services.AddInfrastructure(cfg);

// IDs de aplicación para el log de errores (LOG_ERRORES en dbLumoSys; Seguros=11, Siniestros=12)
builder.Services.Configure<AplicacionOptions>(opts => cfg.GetSection("Aplicaciones").Bind(opts));

// ── Background Services (ETL programado) ───────────────────────────────────
// Los "Intervalo" son un complemento opcional (ventana angosta, cada N min) — el diario
// amplio de arriba SIEMPRE corre, sea cual sea la config de intervalo (ver comentario en
// SegurosEtlIntervaloBackgroundService.cs).
builder.Services.AddHostedService<SegurosEtlBackgroundService>();
builder.Services.AddHostedService<SegurosEtlIntervaloBackgroundService>();
builder.Services.AddHostedService<SiniestrosEtlBackgroundService>();
builder.Services.AddHostedService<SiniestrosEtlIntervaloBackgroundService>();

// ── Controllers ────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Swagger ────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo
    {
        Title       = "LumoSys Integraciones API",
        Version     = "v1",
        Description = "ETL unificado Seguros + Siniestros · SICAS REST → LumoSys"
    });
});

// ── Build ─────────────────────────────────────────────────────────────────
var app = builder.Build();

RegistrarArranque(app);

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opt =>
    {
        opt.SwaggerEndpoint("/swagger/v1/swagger.json", "LumoSys Integraciones v1");
        opt.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();

/// <summary>Deja constancia en el log de con qué configuración arrancó el servicio.
/// Es lo primero que se necesita al diagnosticar: contra qué base y qué SICAS está apuntando,
/// si el modo intervalo quedó activo, y a dónde van los documentos. Nunca imprime credenciales.</summary>
static void RegistrarArranque(WebApplication app)
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arranque");
    var cfg = app.Configuration;

    string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida";
    var compilado = File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location);

    log.LogInformation("===== LumoSysIntegraciones v{Version} arrancando (binario del {Compilado:dd/MM/yyyy HH:mm}) =====",
        version, compilado);
    log.LogInformation("Entorno={Entorno} · Urls={Urls} · MaquinaHost={Maquina}",
        app.Environment.EnvironmentName, cfg["Urls"] ?? "(default)", Environment.MachineName);
    log.LogInformation("BD destino: {Bd}", DescribirConexion(cfg.GetConnectionString("LumoSys")));
    log.LogInformation("SICAS: {Url} · usuario configurado={Tiene}",
        cfg["SICAS:BaseUrl"], !string.IsNullOrWhiteSpace(cfg["SICAS:Usuario"]));
    log.LogInformation("SFleet: {Url} · credenciales configuradas={Tiene}",
        cfg["SFleet:BaseUrl"], !string.IsNullOrWhiteSpace(cfg["SFleet:Email"]));
    log.LogInformation("FTP: {Host} · polizas='{Polizas}' · siniestros='{Siniestros}'",
        cfg["Ftp:Host"], cfg["Ftp:RutaDestinoPolizas"], cfg["Ftp:RutaDestinoSiniestros"]);

    log.LogInformation("ETL diario: Seguros={HoraSeg} Siniestros={HoraSin}",
        cfg["EtlSchedule:Seguros"] ?? "00:05", cfg["EtlSchedule:Siniestros"] ?? "00:10");
    log.LogInformation("ETL intervalo: Seguros cada {IntSeg} min/ventana {VenSeg} · Siniestros cada {IntSin} min/ventana {VenSin} (0 = apagado)",
        cfg["EtlSchedule:IntervaloMinutosSeguros"] ?? "0", cfg["EtlSchedule:VentanaMinutosSeguros"] ?? "0",
        cfg["EtlSchedule:IntervaloMinutosSiniestros"] ?? "0", cfg["EtlSchedule:VentanaMinutosSiniestros"] ?? "0");

    var logOpts = app.Services.GetRequiredService<IOptions<LogArchivoOptions>>().Value;
    log.LogInformation("Log de archivo: carpeta='{Carpeta}' retencion={Dias}d supresion={Sup}min",
        logOpts.Carpeta, logOpts.RetencionDias, logOpts.SupresionMinutos);
}

/// <summary>Extrae solo servidor y catálogo de la cadena de conexión — el resto lleva la contraseña.</summary>
static string DescribirConexion(string? cadena)
{
    if (string.IsNullOrWhiteSpace(cadena)) return "(sin configurar)";

    try
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cadena);
        return $"{b.DataSource}/{b.InitialCatalog}";
    }
    catch
    {
        return "(cadena no parseable)";
    }
}
