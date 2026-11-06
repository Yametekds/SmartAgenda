namespace SmartAgenda.Services;

/// <summary>
/// Dónde guarda la app sus datos (tareas, modelos de voz, token de Google). En el PC es la
/// carpeta Data/ junto a la app; en Android, la carpeta privada de la app.
/// </summary>
public interface IRutasApp
{
    string CarpetaDatos { get; }
}
