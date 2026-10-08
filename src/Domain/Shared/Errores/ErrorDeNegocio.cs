namespace LumoSys.Integraciones.Domain.Shared.Errores;

/// <summary>
/// Un dato no cumple una regla del negocio o falta en un catálogo: la aseguradora no está dada de
/// alta, el tipo de siniestro no existe, el estatus llega vacío. El proceso no puede continuar con
/// ESE registro, pero el sistema está perfectamente sano.
///
/// Existe para poder distinguirlos de los fallos de infraestructura. Antes todos se lanzaban como
/// <see cref="InvalidOperationException"/>, de modo que "la aseguradora es obligatoria" y "se cayó
/// la base de datos" llegaban a Sentry como el mismo tipo de evento y con la misma severidad; sin
/// una diferencia en el tipo, lo único que quedaba para clasificarlos era el texto del mensaje.
///
/// Hereda de <see cref="InvalidOperationException"/> a propósito: conserva el comportamiento de
/// todo el código que ya existía cuando estos errores se lanzaban así, y el cambio no altera
/// ningún flujo.
/// </summary>
public class ErrorDeNegocio(string mensaje) : InvalidOperationException(mensaje);
