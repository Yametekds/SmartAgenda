namespace SmartAgenda.Models;

/// <summary>Valores que el usuario acaba de editar en una tarea ya guardada.</summary>
public record EdicionTarea(Guid Id, string Texto, DateOnly Fecha, TimeOnly? Hora, int DuracionMinutos);
