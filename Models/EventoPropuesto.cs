namespace SmartAgenda.Models;

/// <summary>Un evento detectado en el texto/audio, pendiente de confirmación antes de guardarse.</summary>
public class EventoPropuesto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Texto { get; set; } = string.Empty;
    public DateOnly Fecha { get; set; }
    public TimeOnly? Hora { get; set; }
    public TimeSpan? Duracion { get; set; }
    public bool EsAmbiguo { get; set; }
    public string? MotivoAmbiguedad { get; set; }

    /// <summary>
    /// Si es false, esta ambigüedad nunca se da por buena en silencio aunque sea el único evento
    /// de la entrada (p. ej. un número suelto sin mes ni día de la semana que lo respalde).
    /// </summary>
    public bool PermiteSupresionSiUnico { get; set; } = true;
}
