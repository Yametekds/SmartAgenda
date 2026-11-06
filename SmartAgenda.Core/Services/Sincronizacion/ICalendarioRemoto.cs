using SmartAgenda.Models;

namespace SmartAgenda.Services.Sincronizacion;

/// <summary>
/// El calendario compartido con el que se sincroniza cada dispositivo (Google Calendar). Separado
/// en una interfaz para poder probar la sincronización sin conexión a Google.
/// </summary>
public interface ICalendarioRemoto
{
    /// <summary>Hay una cuenta conectada con la que sincronizar.</summary>
    bool Disponible { get; }

    /// <summary>Crea o actualiza el evento de la tarea y devuelve su id en el calendario.</summary>
    Task<string> GuardarAsync(TareaItem tarea, CancellationToken ct = default);

    /// <summary>Borra el evento; si ya no existe, no hace nada.</summary>
    Task EliminarAsync(string eventoId, CancellationToken ct = default);

    /// <summary>
    /// Eventos entre <paramref name="desde"/> y <paramref name="hasta"/> que cambiaron después de
    /// <paramref name="modificadosDesde"/> (todos si es null), incluidos los borrados.
    /// </summary>
    Task<List<EventoRemoto>> ObtenerCambiosAsync(DateOnly desde, DateOnly hasta, DateTimeOffset? modificadosDesde, CancellationToken ct = default);
}

/// <summary>Un evento del calendario remoto, ya traducido a lo que entiende la agenda.</summary>
public sealed record EventoRemoto
{
    public required string Id { get; init; }
    public bool Cancelado { get; init; }

    /// <summary>Id de la tarea en Smart Agenda, si el evento lo creó (o ya lo conoce) la app.</summary>
    public Guid? SmartagendaId { get; init; }

    public string Texto { get; init; } = string.Empty;
    public DateOnly Fecha { get; init; }
    public TimeOnly? Hora { get; init; }
    public TimeSpan? Duracion { get; init; }

    /// <summary>Estado de completada guardado por la app; null si el evento no lo tiene.</summary>
    public bool? Completada { get; init; }

    /// <summary>Último cambio del evento en el calendario (UTC).</summary>
    public DateTime Actualizado { get; init; }
}
