using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

public class GoogleCalendarService
{
    public const string RutaConectar = "/google/conectar";
    public const string RutaCallback = "/google/callback";

    // Clave con la que se guarda el token en Data/google-token/. Es la misma que usaba la versión
    // anterior (GoogleWebAuthorizationBroker), así que un token ya guardado sigue sirviendo.
    private const string UsuarioToken = "usuario-local";

    private readonly string _credencialesPath;
    private readonly string _tokenStorePath;
    private readonly ILogger<GoogleCalendarService> _logger;
    private GoogleAuthorizationCodeFlow? _flujo;
    private CalendarService? _servicio;

    public GoogleCalendarService(IWebHostEnvironment env, IConfiguration config, ILogger<GoogleCalendarService> logger)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _credencialesPath = config["GoogleCalendar:CredentialsPath"]
            ?? Path.Combine(dataDir, "credentials.json");
        _tokenStorePath = Path.Combine(dataDir, "google-token");
        _logger = logger;
    }

    /// <summary>Hay un credentials.json propio del proyecto (requisito técnico de Google, se crea una sola vez).</summary>
    public bool EstaConfigurado => File.Exists(_credencialesPath);

    /// <summary>Ya se completó el inicio de sesión con Google y hay un token utilizable guardado localmente.</summary>
    public bool EstaConectado => _servicio != null || Directory.Exists(_tokenStorePath) && Directory.EnumerateFiles(_tokenStorePath).Any();

    /// <summary>
    /// URL del inicio de sesión de Google. El navegador del usuario (el del móvil o el del PC, el que
    /// esté usando la app) va ahí y Google le devuelve a <paramref name="redirectUri"/> con un código.
    /// Antes se abría un navegador en el servidor, que desde el móvil nunca llegaba a verse.
    /// </summary>
    public string CrearUrlAutorizacion(string redirectUri, string state)
    {
        var url = ObtenerFlujo().CreateAuthorizationCodeRequest(redirectUri);
        url.State = state;
        return url.Build().AbsoluteUri;
    }

    /// <summary>Canjea el código que devuelve Google por un token y lo guarda en Data/google-token/.</summary>
    public async Task CompletarAutorizacionAsync(string codigo, string redirectUri, CancellationToken ct)
    {
        await ObtenerFlujo().ExchangeCodeForTokenAsync(UsuarioToken, codigo, redirectUri, ct);
        _servicio = null;
    }

    /// <summary>
    /// Olvida la sesión guardada localmente y, si se puede, revoca el permiso en Google para que el
    /// token no siga siendo válido aunque alguien tuviera una copia.
    /// </summary>
    public async Task DesconectarAsync()
    {
        _servicio = null;

        if (EstaConfigurado)
        {
            try
            {
                var flujo = ObtenerFlujo();
                var token = await flujo.LoadTokenAsync(UsuarioToken, CancellationToken.None);
                if (token is not null)
                {
                    await flujo.RevokeTokenAsync(UsuarioToken, token.RefreshToken ?? token.AccessToken, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo revocar el token de Google; se borra solo la copia local.");
            }
        }

        if (Directory.Exists(_tokenStorePath))
        {
            Directory.Delete(_tokenStorePath, recursive: true);
        }
    }

    private GoogleAuthorizationCodeFlow ObtenerFlujo()
    {
        if (_flujo != null) return _flujo;

        if (!EstaConfigurado)
        {
            throw new InvalidOperationException(
                $"No se encontró el archivo de credenciales de Google en '{_credencialesPath}'. " +
                "Sigue las instrucciones del README para crear tu proyecto OAuth en Google Cloud Console (solo hay que hacerlo una vez).");
        }

        using var stream = new FileStream(_credencialesPath, FileMode.Open, FileAccess.Read);
        _flujo = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes = new[] { CalendarService.Scope.Calendar },
            DataStore = new FileDataStore(_tokenStorePath, true),
            // "consent" garantiza que Google entregue un refresh token aunque ya se hubiera dado
            // permiso antes; sin él, la conexión caducaría a la hora.
            Prompt = "consent"
        });
        return _flujo;
    }

    private async Task<CalendarService> ObtenerServicioAsync()
    {
        if (_servicio != null) return _servicio;

        var flujo = ObtenerFlujo();
        var token = await flujo.LoadTokenAsync(UsuarioToken, CancellationToken.None)
            ?? throw new InvalidOperationException("Google Calendar no está conectado. Pulsa \"Conectar con Google\".");

        _servicio = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new UserCredential(flujo, UsuarioToken, token),
            ApplicationName = "Smart Agenda"
        });

        return _servicio;
    }

    public async Task<string> CrearOActualizarEventoAsync(TareaItem tarea, DateOnly fecha)
    {
        var servicio = await ObtenerServicioAsync();

        Event evento;
        if (tarea.Hora is { } hora)
        {
            var inicio = fecha.ToDateTime(hora);
            var fin = inicio.Add(tarea.Duracion ?? TimeSpan.FromHours(1));
            var zonaHoraria = ObtenerZonaHorariaIana();
            evento = new Event
            {
                Summary = tarea.Texto,
                Start = new EventDateTime { DateTimeDateTimeOffset = inicio, TimeZone = zonaHoraria },
                End = new EventDateTime { DateTimeDateTimeOffset = fin, TimeZone = zonaHoraria }
            };
        }
        else
        {
            evento = new Event
            {
                Summary = tarea.Texto,
                Start = new EventDateTime { Date = fecha.ToString("yyyy-MM-dd") },
                End = new EventDateTime { Date = fecha.AddDays(1).ToString("yyyy-MM-dd") }
            };
        }

        if (!string.IsNullOrEmpty(tarea.GoogleEventId))
        {
            try
            {
                var actualizado = await servicio.Events.Update(evento, "primary", tarea.GoogleEventId).ExecuteAsync();
                return actualizado.Id;
            }
            catch (Google.GoogleApiException)
            {
                // El evento ya no existe en el calendario; se crea uno nuevo.
            }
        }

        var creado = await servicio.Events.Insert(evento, "primary").ExecuteAsync();
        return creado.Id;
    }

    /// <summary>Google Calendar exige IDs de zona horaria IANA; en Windows TimeZoneInfo.Local.Id usa su propio formato.</summary>
    private static string ObtenerZonaHorariaIana()
    {
        var id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }

    public async Task EliminarEventoAsync(string googleEventId)
    {
        var servicio = await ObtenerServicioAsync();
        try
        {
            await servicio.Events.Delete("primary", googleEventId).ExecuteAsync();
        }
        catch (Google.GoogleApiException)
        {
            // Ya no existe; no es un error para el usuario.
        }
    }

    /// <summary>Trae los eventos del calendario principal de Google para los 7 días que empiezan en <paramref name="lunes"/>.</summary>
    public async Task<List<Event>> ObtenerEventosSemanaAsync(DateOnly lunes)
    {
        var servicio = await ObtenerServicioAsync();

        var solicitud = servicio.Events.List("primary");
        solicitud.TimeMinDateTimeOffset = lunes.ToDateTime(TimeOnly.MinValue);
        solicitud.TimeMaxDateTimeOffset = lunes.AddDays(7).ToDateTime(TimeOnly.MinValue);
        solicitud.SingleEvents = true;
        // Incluye los eventos borrados en Google (Status = "cancelled") para poder borrarlos también aquí.
        solicitud.ShowDeleted = true;
        solicitud.MaxResults = 2500;

        var resultado = await solicitud.ExecuteAsync();
        return resultado.Items?.ToList() ?? new List<Event>();
    }

    /// <summary>Un evento concreto por su id, o null si ya no existe en el calendario.</summary>
    public async Task<Event?> ObtenerEventoAsync(string googleEventId)
    {
        var servicio = await ObtenerServicioAsync();
        try
        {
            return await servicio.Events.Get("primary", googleEventId).ExecuteAsync();
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return null;
        }
    }
}
