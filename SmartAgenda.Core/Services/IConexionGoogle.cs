namespace SmartAgenda.Services;

/// <summary>
/// Inicia el inicio de sesión con Google. Cada plataforma lo hace a su manera: la web redirige
/// el navegador a la página de Google y vuelve a /google/callback; Android usa el flujo nativo.
/// </summary>
public interface IConexionGoogle
{
    Task IniciarAsync();
}
