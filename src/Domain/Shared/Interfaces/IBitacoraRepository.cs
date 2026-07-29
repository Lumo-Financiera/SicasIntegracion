namespace LumoSys.Integraciones.Domain.Shared.Interfaces;

public enum NivelBitacora { Error, Critico, Aviso }

public interface IBitacoraRepository
{
    Task GuardarAsync(string descripcion, NivelBitacora nivel, int idAplicacion, CancellationToken ct = default);
}
