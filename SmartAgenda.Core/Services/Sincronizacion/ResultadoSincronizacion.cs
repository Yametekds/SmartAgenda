namespace SmartAgenda.Services.Sincronizacion;

public sealed record ResultadoSincronizacion
{
    /// <summary>No había cuenta conectada: no se hizo nada.</summary>
    public bool SinConexion { get; init; }

    /// <summary>Tareas nuevas que llegaron desde el calendario.</summary>
    public int Nuevas { get; init; }

    /// <summary>Tareas de aquí actualizadas con cambios hechos en otro sitio.</summary>
    public int Actualizadas { get; init; }

    /// <summary>Tareas de aquí borradas porque se borraron en otro sitio.</summary>
    public int Eliminadas { get; init; }

    /// <summary>Cambios de aquí enviados al calendario.</summary>
    public int Enviadas { get; init; }

    /// <summary>Cambios de aquí que no se pudieron enviar (se reintentan en la siguiente sincronización).</summary>
    public int Errores { get; init; }

    /// <summary>Si falló la sincronización entera (sin red, permiso caducado...), el motivo.</summary>
    public string? Error { get; init; }

    public bool HuboCambiosLocales => Nuevas + Actualizadas + Eliminadas > 0;

    public static ResultadoSincronizacion NoConectado { get; } = new() { SinConexion = true };
}
