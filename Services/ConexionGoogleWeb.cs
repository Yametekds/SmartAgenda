using Microsoft.AspNetCore.Components;

namespace SmartAgenda.Services;

/// <summary>
/// En la web, el inicio de sesión de Google se hace en el mismo navegador (móvil o PC): se sale
/// de la app a la página de Google y se vuelve a /google/callback, que deja el token guardado.
/// </summary>
public class ConexionGoogleWeb(NavigationManager navigation) : IConexionGoogle
{
    public Task IniciarAsync()
    {
        navigation.NavigateTo(GoogleCalendarService.RutaConectar, forceLoad: true);
        return Task.CompletedTask;
    }
}
