using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartAgenda.Services;

namespace SmartAgenda.Movil.Services;

/// <summary>
/// Inicio de sesión con Google en Android: se abre el navegador del móvil con la página de Google
/// y, al dar permiso, Google vuelve a http://localhost:53682/, donde la propia app está escuchando
/// para recoger el código. Así no hace falta servidor ni configurar esquemas de URL propios.
/// </summary>
public class ConexionGoogleMovil(GoogleCalendarService google, ILogger<ConexionGoogleMovil> logger) : IConexionGoogle
{
    public const int Puerto = 53682;
    public static readonly string DireccionVuelta = $"http://localhost:{Puerto}/";
    private static readonly TimeSpan Limite = TimeSpan.FromMinutes(5);

    public bool PuedeImportarCredenciales => true;

    public async Task IniciarAsync()
    {
        var estado = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var escucha = new HttpListener();
        escucha.Prefixes.Add(DireccionVuelta);
        escucha.Start();

        await Browser.Default.OpenAsync(google.CrearUrlAutorizacion(DireccionVuelta, estado), BrowserLaunchMode.SystemPreferred);

        using var limite = new CancellationTokenSource(Limite);
        var peticion = escucha.GetContextAsync();
        if (await Task.WhenAny(peticion, Task.Delay(Limite, limite.Token)) != peticion)
        {
            logger.LogWarning("No llegó la respuesta de Google en {Minutos} minutos.", Limite.TotalMinutes);
            return;
        }

        var contexto = await peticion;
        var codigo = contexto.Request.QueryString["code"];
        var estadoRecibido = contexto.Request.QueryString["state"];
        var valido = !string.IsNullOrEmpty(codigo) && estadoRecibido == estado;

        await ResponderAsync(contexto.Response, valido);

        if (!valido)
        {
            logger.LogWarning("Vuelta de Google rechazada (error: {Error}).", contexto.Request.QueryString["error"]);
            return;
        }

        await google.CompletarAutorizacionAsync(codigo!, DireccionVuelta, limite.Token);
    }

    /// <summary>Página que ve el usuario en el navegador al terminar: le dice que vuelva a la app.</summary>
    private static async Task ResponderAsync(HttpListenerResponse respuesta, bool correcto)
    {
        var (titulo, texto) = correcto
            ? ("Listo · Done", "Ya puedes volver a Smart Agenda.<br>You can go back to Smart Agenda now.")
            : ("No se pudo conectar · Couldn't connect", "Vuelve a Smart Agenda e inténtalo de nuevo.<br>Go back to Smart Agenda and try again.");
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Smart Agenda</title></head>
            <body style="background:#12181c;color:#f2f2f0;font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:90vh;text-align:center">
            <div><h1 style="font-size:1.5rem">{{titulo}}</h1><p style="color:#c3bc9e;line-height:1.6">{{texto}}</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        respuesta.ContentType = "text/html; charset=utf-8";
        respuesta.ContentLength64 = bytes.Length;
        await respuesta.OutputStream.WriteAsync(bytes);
        respuesta.Close();
    }

    public async Task<bool> ImportarCredencialesAsync()
    {
        var archivo = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "credentials.json" });
        if (archivo is null) return false;

        await using var origen = await archivo.OpenReadAsync();
        using var copia = new MemoryStream();
        await origen.CopyToAsync(copia);

        if (!EsCredencialDeGoogle(copia.ToArray())) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(google.RutaCredenciales)!);
        await File.WriteAllBytesAsync(google.RutaCredenciales, copia.ToArray());
        google.OlvidarCredenciales();
        return true;
    }

    /// <summary>Un credentials.json de Google tiene un objeto "installed" o "web" con client_id.</summary>
    private static bool EsCredencialDeGoogle(byte[] contenido)
    {
        try
        {
            using var json = JsonDocument.Parse(contenido);
            foreach (var tipo in new[] { "installed", "web" })
            {
                if (json.RootElement.TryGetProperty(tipo, out var datos) && datos.TryGetProperty("client_id", out _)) return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }
}
