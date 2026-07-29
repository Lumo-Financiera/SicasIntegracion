using LumoSys.Integraciones.API.BackgroundServices;
using LumoSys.Integraciones.API.Middlewares;
using LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;
using LumoSys.Integraciones.Application.Seguros.UseCases.ProcesarLoteSeguros;
using Microsoft.Extensions.Options;
using LumoSys.Integraciones.Application.Seguros.UseCases.SubirDocumentoPoliza;
using LumoSys.Integraciones.Application.Siniestros.UseCases.GuardarSiniestro;
using LumoSys.Integraciones.Application.Siniestros.UseCases.ProcesarLoteSiniestros;
using LumoSys.Integraciones.Infrastructure.Extensions;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Permite correr como servicio de Windows en producción (sc.exe create ...).
// Sin efecto cuando se ejecuta interactivo (dotnet run / consola).
builder.Host.UseWindowsService(opts => opts.ServiceName = "LumoSysIntegraciones");

// appsettings.Local.json: credenciales reales para pruebas locales, nunca se versiona.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var cfg = builder.Configuration;

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
