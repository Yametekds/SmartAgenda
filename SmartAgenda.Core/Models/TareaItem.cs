namespace SmartAgenda.Models;

public class TareaItem
{
    /// <summary>Identificador fijo de la tarea, el mismo en todos los dispositivos (va también en el evento de Google).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Texto { get; set; } = string.Empty;
    public TimeOnly? Hora { get; set; }
    public TimeSpan? Duracion { get; set; }
    public DateOnly Fecha { get; set; }
    public bool Completada { get; set; }
    public string? GoogleEventId { get; set; }
    public DateTime CreadaEn { get; set; } = DateTime.Now;

    /// <summary>Último cambio (UTC). Al sincronizar, entre dos versiones distintas gana la más reciente.</summary>
    public DateTime ModificadaEn { get; set; }

    /// <summary>Hay un cambio hecho en este dispositivo que todavía no ha llegado a Google Calendar.</summary>
    public bool PendienteSync { get; set; }

    /// <summary>
    /// Borrada aquí, pero el borrado aún no ha llegado a Google. Se guarda la marca en vez de quitarla
    /// del todo para que la siguiente sincronización no la vuelva a traer.
    /// </summary>
    public bool Eliminada { get; set; }
}
