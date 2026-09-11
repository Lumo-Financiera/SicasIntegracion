using LumoSys.Integraciones.API.BackgroundServices;
using LumoSys.Integraciones.API.Extensions;
using LumoSys.Integraciones.API.Filters;
using LumoSys.Integraciones.API.Middlewares;
using LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;
using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using Microsoft.Extensions.Options;
using LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;
using LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;
using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;

// Todo el arranque va dentro del try: corriendo como servicio de Windows, un fallo aquí no deja
// más rastro que un servicio que no levanta (ver SentryStartupExtensions.ReportarFallaDeArranque).
try
{
    var builder = WebApplication.CreateBuilder(args);

    // Permite correr como servicio de Windows en producción (sc.exe create ...).
    // Sin efecto cuando se ejecuta interactivo (dotnet run / consola).
    builder.Host.UseWindowsService(opts => opts.ServiceName = "LumoSysIntegraciones");

    // appsettings.Local.json: credenciales reales, nunca se versiona (está en .gitignore).
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

    var cfg = builder.Configuration;

    // ── Monitoreo (Sentry) ───────────────────────────────────────────────────
    // Va antes que cualquier otro registro: así las fallas de arranque de los servicios de abajo
    // (cadena de conexión inválida, config faltante) también quedan reportadas.
    // Debe cargarse después de appsettings.Local.json para poder sobreescribir el DSN por ambiente.
    builder.ConfigurarSentry();

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
    // El filtro de monitoreo es global a propósito: etiqueta cada petición con su controlador,
    // acción e identificadores de negocio sin que los controllers dejen de ser thin, y cubre
    // también los endpoints que se agreguen después.
    builder.Services.AddControllers(opt => opt.Filters.Add<MonitoreoActionFilter>());

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

    // Marca el apagado para que Sentry pueda distinguir las cancelaciones del paro del servicio
    // (ruido esperable) de un Timeout de HttpClient contra SICAS (falla real). Ver ApagadoEnCurso.
    app.Lifetime.ApplicationStopping.Register(ApagadoEnCurso.Marcar);

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
}
catch (Exception ex)
{
    SentryStartupExtensions.ReportarFallaDeArranque(ex);
    throw; // el Visor de eventos de Windows tiene que seguir viendo el fallo original
}
