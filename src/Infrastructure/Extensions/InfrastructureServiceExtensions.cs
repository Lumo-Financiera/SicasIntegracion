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
        services.Configure<SICASOptions>(opts   => config.GetSection("SICAS").Bind(opts));
        services.Configure<SFleetOptions>(opts  => config.GetSection("SFleet").Bind(opts));
        services.Configure<FtpOptions>(opts     => config.GetSection("Ftp").Bind(opts));

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
        services.AddSingleton<LogErroresArchivoService>();

        // Servicios externos
        services.AddScoped<IDocumentService, FtpDocumentService>();
        services.AddScoped<ISFleetClient, SFleetClient>();

        return services;
    }
}
