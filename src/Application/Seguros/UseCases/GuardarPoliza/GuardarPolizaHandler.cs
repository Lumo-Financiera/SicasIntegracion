using LumoSys.Integraciones.Domain.Seguros.Interfaces;
using LumoSys.Integraciones.Domain.Seguros.Models;
using Microsoft.Extensions.Logging;

namespace LumoSys.Integraciones.Application.Seguros.UseCases.GuardarPoliza;

public sealed class GuardarPolizaHandler(IPolizaRepository repo, ILogger<GuardarPolizaHandler> log)
{
    public async Task<GuardarPolizaResult> Handle(GuardarPolizaCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Poliza))
            return GuardarPolizaResult.Fallo("Número de póliza requerido.");

        if (cmd.Vehiculo is null || string.IsNullOrWhiteSpace(cmd.Vehiculo.Serie))
            return GuardarPolizaResult.Fallo("Datos del vehículo requeridos.");

        try
        {
            if (!await repo.ExisteVehiculoAsync(cmd.Vehiculo.Serie, ct))
            {
                string msg = $"Serie {cmd.Vehiculo.Serie} no está registrada como vehículo propio " +
                             "(no existe en COMPRAS_DETALLES ni VEHICULOS); se omite, no pertenece a esta flotilla.";
                log.LogInformation(msg);
                return GuardarPolizaResult.Omitir(msg);
            }

            var datosPoliza = new DatosPoliza
            {
                NumeroPoliza       = cmd.Poliza,
                Aseguradora        = cmd.Aseguradora,
                Beneficiario       = cmd.Beneficiario,
                BeneficiarioPreferente = cmd.BeneficiarioPreferente,
                Broker             = cmd.Broker,
                FormaPago          = cmd.FormaPago,
                Contratante        = cmd.Contratante
            };

            int polizaId = await repo.UpsertPolizaAsync(datosPoliza, ct);

            var datosVehiculo = new DatosVehiculo
            {
                NumeroPoliza         = cmd.Poliza,
                Serie                = cmd.Vehiculo.Serie,
                Inciso               = cmd.Vehiculo.Inciso,
                TipoPoliza           = cmd.Vehiculo.TipoPoliza,
                FechaInicio          = ParseFecha(cmd.Vehiculo.FechaInicio),
                FechaVencimiento     = ParseFecha(cmd.Vehiculo.FechaVencimiento),
                PrimaNeta            = cmd.Vehiculo.PrimaNeta,
                PrimaTotal           = cmd.Vehiculo.PrimaTotal,
                Derechos             = cmd.Vehiculo.Derechos,
                Recargos             = cmd.Vehiculo.Recargos,
                IVA                  = cmd.Vehiculo.IVA,
                PrimerRecibo         = cmd.Vehiculo.PrimerRecibo,
                Subsecuente          = cmd.Vehiculo.Subsecuente,
                TipoUso              = cmd.Vehiculo.TipoUso,
                AdministracionCartera = cmd.Vehiculo.AdministracionCartera,
                Ejecutivo            = cmd.Vehiculo.Ejecutivo,
                Pasajeros            = cmd.Vehiculo.Pasajeros,
                GestionPago          = cmd.Vehiculo.GestionPago,
                Cobertura            = cmd.Vehiculo.Cobertura,
                Adaptacion           = cmd.Vehiculo.Adaptacion,
                ValorAdaptacion      = cmd.Vehiculo.ValorAdaptacion,
                CoberturasDanosMateriales = cmd.Vehiculo.CoberturasDanos,
                DeducibleDanos       = cmd.Vehiculo.DeducibleDanos,
                CoberturasRoboTotal  = cmd.Vehiculo.CoberturasRoboTotal,
                DeducibleRoboTotal   = cmd.Vehiculo.DeducibleRoboTotal,
                RoboParcial          = cmd.Vehiculo.RoboParcial,
                DeducibleRoboParcial = cmd.Vehiculo.DeducibleRoboParcial,
                AdaptacionesConversiones = cmd.Vehiculo.AdaptacionesConversiones,
                AccidentesConductor  = cmd.Vehiculo.AccidentesConductor,
                GastosMedicosOcupantes = cmd.Vehiculo.GastosMedicosOcupantes,
                AsistenciaJuridica   = cmd.Vehiculo.AsistenciaJuridica,
                AsistenciaVial       = cmd.Vehiculo.AsistenciaVial,
                ResponsabilidadCivilCruzada = cmd.Vehiculo.ResponsabilidadCivilCruzada,
                DeducibleResponsabilidadCivil = cmd.Vehiculo.DeducibleResponsabilidadCivil,
                ResponsabilidadCivilLimiteUnicoCombinado = cmd.Vehiculo.ResponsabilidadCivilLimiteUnicoCombinado,
                ResponsabilidadCivilPasajeros = cmd.Vehiculo.ResponsabilidadCivilPasajeros,
                ResponsabilidadCivilExtranjero = cmd.Vehiculo.ResponsabilidadCivilExtranjero,
                ValorFactura         = cmd.Vehiculo.ValorFactura,
                TipoActivo           = cmd.Vehiculo.TipoActivo
            };

            int vehiculoId = await repo.UpsertVehiculoAsync(datosVehiculo, polizaId, ct);

            log.LogInformation("Póliza {Poliza} guardada. PolizaId={PolizaId} VehiculoId={VehiculoId}",
                cmd.Poliza, polizaId, vehiculoId);

            return GuardarPolizaResult.Ok(polizaId, vehiculoId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error al guardar póliza {Poliza}", cmd.Poliza);
            return GuardarPolizaResult.Fallo(ex.Message);
        }
    }

    private static DateTime? ParseFecha(string? valor) =>
        DateTime.TryParse(valor, out var d) ? d : null;
}
