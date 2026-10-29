using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SmartAgenda.Services;

/// <summary>
/// Contraseña de acceso a la app. Sin ella, cualquiera que llegara al puerto (otro equipo de la
/// Wi-Fi, otro dispositivo de la tailnet) podía ver y borrar la agenda y usar la cuenta de Google
/// conectada. Desde el propio equipo donde corre la app (localhost) no se pide, salvo que se
/// desactive con "Acceso:PermitirLocalSinContrasena": false.
///
/// La contraseña sale, por orden, de la configuración ("Acceso:Contrasena", p. ej. con la variable
/// de entorno Acceso__Contrasena) o del archivo Data/acceso.pwd. Si no hay ninguna, se genera una
/// aleatoria la primera vez, se guarda en ese archivo y se muestra en la consola al arrancar.
/// </summary>
public class AccesoService
{
    public const string RutaAcceso = "/acceso";

    // Sin caracteres que se confundan entre sí al teclearlos en el móvil (0/O, 1/l/I).
    private const string AlfabetoContrasena = "abcdefghjkmnpqrstuvwxyz23456789";

    private readonly byte[] _hashContrasena;

    public bool PermitirLocalSinContrasena { get; }

    public AccesoService(IWebHostEnvironment env, IConfiguration config, ILogger<AccesoService> logger)
    {
        PermitirLocalSinContrasena = config.GetValue("Acceso:PermitirLocalSinContrasena", true);

        var contrasena = config["Acceso:Contrasena"];
        if (string.IsNullOrWhiteSpace(contrasena))
        {
            var ruta = Path.Combine(env.ContentRootPath, "Data", "acceso.pwd");
            if (File.Exists(ruta))
            {
                contrasena = File.ReadAllText(ruta).Trim();
            }

            if (string.IsNullOrWhiteSpace(contrasena))
            {
                contrasena = GenerarContrasena();
                Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
                File.WriteAllText(ruta, contrasena);
                logger.LogWarning(
                    "Se ha creado la contraseña de acceso para otros dispositivos: {Contrasena} (guardada en {Ruta}; cámbiala editando ese archivo).",
                    contrasena, ruta);
            }
        }

        _hashContrasena = SHA256.HashData(Encoding.UTF8.GetBytes(contrasena));
    }

    public bool EsCorrecta(string? intento) =>
        intento is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(intento.Trim())), _hashContrasena);

    /// <summary>La petición viene del propio equipo donde corre la app.</summary>
    public static bool EsLocal(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>Solo rutas de esta misma app, para que "volver" no sirva para redirigir a otra web.</summary>
    public static string RutaDeVueltaSegura(string? volver) =>
        !string.IsNullOrEmpty(volver) && volver.StartsWith('/') && !volver.StartsWith("//") && !volver.StartsWith("/\\")
            ? volver
            : "/";

    private static string GenerarContrasena()
    {
        var caracteres = new char[12];
        for (var i = 0; i < caracteres.Length; i++)
        {
            caracteres[i] = AlfabetoContrasena[RandomNumberGenerator.GetInt32(AlfabetoContrasena.Length)];
        }
        // En grupos de 4 ("abcd-efgh-jkmn"), más fácil de copiar a mano.
        return $"{new string(caracteres, 0, 4)}-{new string(caracteres, 4, 4)}-{new string(caracteres, 8, 4)}";
    }

    /// <summary>Página de acceso: HTML suelto con los mismos colores y tipografía que la app.</summary>
    public static string PaginaAcceso(bool ingles, string volver, bool error)
    {
        var titulo = ingles ? "Enter your password" : "Introduce tu contraseña";
        var ayuda = ingles
            ? "You'll find it in the <code>Data/acceso.pwd</code> file on the computer running Smart Agenda. You'll only need it once on this device."
            : "Está en el archivo <code>Data/acceso.pwd</code> del equipo donde corre Smart Agenda. Solo tendrás que ponerla una vez en este dispositivo.";
        var etiqueta = ingles ? "Password" : "Contraseña";
        var boton = ingles ? "Enter" : "Entrar";
        var mensajeError = error
            ? $"<p class=\"error\">{(ingles ? "Wrong password." : "Contraseña incorrecta.")}</p>"
            : "";

        return $$"""
            <!DOCTYPE html>
            <html lang="{{(ingles ? "en" : "es")}}">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <meta name="theme-color" content="#12181c" />
                <title>Smart Agenda</title>
                <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Roboto+Flex:opsz,wght@8..144,400;8..144,700;8..144,900&display=swap" />
                <style>
                    body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                           background: #12181c; color: #f2f2f0; font-family: "Roboto Flex", "Helvetica Neue", Arial, sans-serif; }
                    main { width: min(360px, calc(100% - 32px)); background: #1a2228; border: 1px solid #2c3840; border-radius: 14px; padding: 1.5rem; }
                    h1 { font-weight: 900; font-size: 1.4rem; margin: 0 0 0.25rem; }
                    h1 strong { color: #e40712; }
                    h2 { font-size: 1rem; margin: 1rem 0 0.4rem; }
                    p { color: #9aa5ab; font-size: 0.85rem; margin: 0 0 1rem; }
                    code { color: #c3bc9e; }
                    label { display: block; font-size: 0.8rem; margin-bottom: 0.3rem; }
                    input { box-sizing: border-box; width: 100%; padding: 0.6rem; border-radius: 4px; border: 1px solid #2c3840;
                            background: #212b32; color: #f2f2f0; font-size: 1rem; }
                    button { margin-top: 1rem; width: 100%; padding: 0.7rem; border: 0; border-radius: 4px; background: #e40712; color: #fff;
                             font-weight: 700; text-transform: uppercase; letter-spacing: 0.02em; cursor: pointer; }
                    .error { color: #e40712; font-weight: 700; margin: 0.6rem 0 0; }
                </style>
            </head>
            <body>
                <main>
                    <h1>Smart<strong>Agenda</strong></h1>
                    <h2>{{titulo}}</h2>
                    <p>{{ayuda}}</p>
                    <form method="post" action="{{RutaAcceso}}">
                        <input type="hidden" name="volver" value="{{WebUtility.HtmlEncode(volver)}}" />
                        <label for="contrasena">{{etiqueta}}</label>
                        <input id="contrasena" name="contrasena" type="password" autocomplete="current-password" autofocus required />
                        {{mensajeError}}
                        <button type="submit">{{boton}}</button>
                    </form>
                </main>
            </body>
            </html>
            """;
    }
}
