namespace SmartAgenda.Models;

public class TareaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Texto { get; set; } = string.Empty;
    public TimeOnly? Hora { get; set; }
    public TimeSpan? Duracion { get; set; }
    public DateOnly Fecha { get; set; }
    public bool Completada { get; set; }
    public string? GoogleEventId { get; set; }
    public DateTime CreadaEn { get; set; } = DateTime.Now;
}
