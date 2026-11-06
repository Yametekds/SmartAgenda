namespace SmartAgenda.Services;

/// <summary>
/// Inicia el inicio de sesión con Google. Cada plataforma lo hace a su manera: la web redirige
/// el navegador a la página de Google y vuelve a /google/callback; Android abre el navegador del
/// móvil y recoge la respuesta dentro de la propia app.
/// </summary>
public interface IConexionGoogle
{
    /// <summary>
    /// Empieza el inicio de sesión. En la web sale de la página (no vuelve); en el móvil termina
    /// cuando el usuario ha dado (o no) el permiso.
    /// </summary>
    Task IniciarAsync();

    /// <summary>La plataforma permite elegir aquí el archivo de credenciales de Google (credentials.json).</summary>
    bool PuedeImportarCredenciales => false;

    /// <summary>Pide al usuario el archivo de credenciales y lo guarda. Devuelve false si se canceló o no es válido.</summary>
    Task<bool> ImportarCredencialesAsync() => Task.FromResult(false);
}
