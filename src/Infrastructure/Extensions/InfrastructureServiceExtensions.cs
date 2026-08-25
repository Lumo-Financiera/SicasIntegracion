using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Shared.Interfaces;
using LumoSys.Integraciones.Domain.Siniestros.Interfaces;
using LumoSys.Integraciones.Infrastructure.Documents;
using LumoSys.Integraciones.Infrastructure.Notifications;
using LumoSys.Integraciones.Infrastructure.Persistence;
using LumoSys.Integraciones.Infrastructure.Persistence.Repositories;
using LumoSys.Integraciones.Infrastructure.SICAS;
using LumoSys.Integraciones.Infrastructure.SFleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        // Bases de datos
        services.AddDbContext<LumoSysContext>(opt =>
            opt.UseSqlServer(config.GetConnectionString("LumoSys")));

        // Opciones tipadas
        services.Configure<SICASOptions>(opts     => config.GetSection("SICAS").Bind(opts));
        services.Configure<SFleetOptions>(opts    => config.GetSection("SFleet").Bind(opts));
        services.Configure<FtpOptions>(opts       => config.GetSection("Ftp").Bind(opts));
        services.Configure<LogArchivoOptions>(opts => config.GetSection("LogArchivo").Bind(opts));

        // SICAS REST (singleton — maneja token con ciclo de vida de 3 min)
        services.AddSingleton<SICASRestClient>();
        services.AddSingleton<ISICASRestClient>(sp => sp.GetRequiredService<SICASRestClient>());

        // Clientes SICAS por dominio (scoped)
        services.AddScoped<ISeguroSICASClient, SICASSeguroClient>();
        services.AddScoped<ISiniestroSICASClient, SICASSiniestroClient>();

        // Repositorios
        services.AddScoped<IPolizaRepository, PolizaRepository>();
        services.AddScoped<ISiniestroRepository, SiniestroRepository>();
        services.AddScoped<IBitacoraRepository, BitacoraRepository>();

        // Servicios externos
        services.AddScoped<IDocumentService, FtpDocumentService>();
        services.AddScoped<ISFleetClient, SFleetClient>();

        return services;
    }

    /// <summary>Registra el log de archivo y lo engancha como proveedor de ILogger.
    ///
    /// Se llama aparte de AddInfrastructure y ANTES de construir el host, porque el
    /// LoggerFactory recoge los ILoggerProvider del contenedor al inicializarse.
    ///
    /// Sin esto, la app corriendo como servicio de Windows no deja rastro de nada: no hay
    /// consola, y UseWindowsService() solo manda al Event Log a partir de Warning.</summary>
    public static IServiceCollection AddLogArchivo(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LogArchivoOptions>(opts => config.GetSection("LogArchivo").Bind(opts));

        services.AddSingleton<LogErroresArchivoService>();
        services.AddSingleton<SupresorErroresRepetidos>();

        // Como ILoggerProvider (no AddProvider) para que reciba sus dependencias por DI.
        services.AddSingleton<ILoggerProvider, ArchivoLoggerProvider>();

        return services;
    }
}
