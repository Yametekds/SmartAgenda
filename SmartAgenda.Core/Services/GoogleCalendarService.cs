using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartAgenda.Models;
using SmartAgenda.Services.Sincronizacion;

namespace SmartAgenda.Services;

public class GoogleCalendarService : ICalendarioRemoto
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

    public GoogleCalendarService(IRutasApp rutas, IConfiguration config, ILogger<GoogleCalendarService> logger)
    {
        var dataDir = rutas.CarpetaDatos;
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

    // Claves de los campos privados que la app guarda en cada evento (solo los ve esta app).
    public const string PropiedadId = "smartagendaId";
    public const string PropiedadCompletada = "completada";

    public bool Disponible => EstaConfigurado && EstaConectado;

    /// <summary>
    /// Crea o actualiza el evento de la tarea. Si ya existe, se parte del evento tal cual está en
    /// Google y solo se cambian título, horario y los campos privados de la app, para no perder lo
    /// que se haya añadido desde el propio Google Calendar (descripción, invitados, avisos...).
    /// </summary>
    public async Task<string> GuardarAsync(TareaItem tarea, CancellationToken ct = default)
    {
        var servicio = await ObtenerServicioAsync();

        Event? existente = null;
        if (!string.IsNullOrEmpty(tarea.GoogleEventId))
        {
            existente = await ObtenerEventoAsync(tarea.GoogleEventId, ct);
            if (existente?.Status == "cancelled") existente = null;
        }

        var evento = existente ?? new Event();
        AplicarTarea(evento, tarea, ObtenerZonaHorariaIana());

        if (existente is not null)
        {
            var actualizado = await servicio.Events.Update(evento, "primary", existente.Id).ExecuteAsync(ct);
            return actualizado.Id;
        }

        var creado = await servicio.Events.Insert(evento, "primary").ExecuteAsync(ct);
        return creado.Id;
    }

    /// <summary>Copia en el evento los datos de la tarea: título, horario y campos privados de la app.</summary>
    public static void AplicarTarea(Event evento, TareaItem tarea, string zonaHoraria)
    {
        evento.Summary = tarea.Texto;

        if (tarea.Hora is { } hora)
        {
            var inicio = tarea.Fecha.ToDateTime(hora);
            var fin = inicio.Add(tarea.Duracion ?? TimeSpan.FromHours(1));
            evento.Start = new EventDateTime { DateTimeDateTimeOffset = inicio, TimeZone = zonaHoraria };
            evento.End = new EventDateTime { DateTimeDateTimeOffset = fin, TimeZone = zonaHoraria };
        }
        else
        {
            evento.Start = new EventDateTime { Date = tarea.Fecha.ToString("yyyy-MM-dd") };
            evento.End = new EventDateTime { Date = tarea.Fecha.AddDays(1).ToString("yyyy-MM-dd") };
        }

        evento.ExtendedProperties ??= new Event.ExtendedPropertiesData();
        evento.ExtendedProperties.Private__ ??= new Dictionary<string, string>();
        evento.ExtendedProperties.Private__[PropiedadId] = tarea.Id.ToString();
        evento.ExtendedProperties.Private__[PropiedadCompletada] = tarea.Completada ? "true" : "false";
    }

    /// <summary>Traduce un evento de Google a lo que entiende la agenda, o null si no tiene una fecha utilizable.</summary>
    public static EventoRemoto? LeerEvento(Event evento)
    {
        if (string.IsNullOrEmpty(evento.Id)) return null;

        Guid? id = null;
        bool? completada = null;
        if (evento.ExtendedProperties?.Private__ is { } privados)
        {
            if (privados.TryGetValue(PropiedadId, out var texto) && Guid.TryParse(texto, out var guid)) id = guid;
            if (privados.TryGetValue(PropiedadCompletada, out var valor) && bool.TryParse(valor, out var c)) completada = c;
        }

        var actualizado = evento.UpdatedDateTimeOffset?.UtcDateTime ?? DateTime.MinValue;

        if (evento.Status == "cancelled")
        {
            return new EventoRemoto { Id = evento.Id, Cancelado = true, SmartagendaId = id, Actualizado = actualizado };
        }

        DateOnly fecha;
        TimeOnly? hora = null;
        TimeSpan? duracion = null;
        if (evento.Start?.DateTimeDateTimeOffset is { } inicio)
        {
            // Pasado a la hora local de este equipo: un evento creado en otra zona horaria debe
            // verse a la hora a la que ocurre aquí, no a la de su zona de origen.
            fecha = DateOnly.FromDateTime(inicio.LocalDateTime);
            hora = TimeOnly.FromDateTime(inicio.LocalDateTime);
            if (evento.End?.DateTimeDateTimeOffset is { } fin) duracion = fin - inicio;
        }
        else if (!string.IsNullOrEmpty(evento.Start?.Date) && DateOnly.TryParseExact(evento.Start.Date, "yyyy-MM-dd", out var fechaSolo))
        {
            fecha = fechaSolo;
        }
        else
        {
            return null;
        }

        return new EventoRemoto
        {
            Id = evento.Id,
            SmartagendaId = id,
            Texto = evento.Summary?.Trim() ?? string.Empty,
            Fecha = fecha,
            Hora = hora,
            Duracion = duracion,
            Completada = completada,
            Actualizado = actualizado
        };
    }

    /// <summary>Google Calendar exige IDs de zona horaria IANA; en Windows TimeZoneInfo.Local.Id usa su propio formato.</summary>
    private static string ObtenerZonaHorariaIana()
    {
        var id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }

    public async Task EliminarAsync(string eventoId, CancellationToken ct = default)
    {
        var servicio = await ObtenerServicioAsync();
        try
        {
            await servicio.Events.Delete("primary", eventoId).ExecuteAsync(ct);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            // Ya no existe; no es un error para el usuario.
        }
    }

    public async Task<List<EventoRemoto>> ObtenerCambiosAsync(DateOnly desde, DateOnly hasta, DateTimeOffset? modificadosDesde, CancellationToken ct = default)
    {
        var servicio = await ObtenerServicioAsync();
        var resultado = new List<EventoRemoto>();
        string? pagina = null;

        do
        {
            var solicitud = servicio.Events.List("primary");
            solicitud.TimeMinDateTimeOffset = desde.ToDateTime(TimeOnly.MinValue);
            solicitud.TimeMaxDateTimeOffset = hasta.ToDateTime(TimeOnly.MinValue);
            solicitud.SingleEvents = true;
            // Incluye los eventos borrados (Status = "cancelled") para poder borrarlos también aquí.
            solicitud.ShowDeleted = true;
            solicitud.MaxResults = 2500;
            solicitud.PageToken = pagina;
            if (modificadosDesde is { } minimo) solicitud.UpdatedMinDateTimeOffset = minimo;

            var respuesta = await solicitud.ExecuteAsync(ct);
            foreach (var evento in respuesta.Items ?? [])
            {
                if (LeerEvento(evento) is { } leido) resultado.Add(leido);
            }
            pagina = respuesta.NextPageToken;
        } while (!string.IsNullOrEmpty(pagina));

        return resultado;
    }

    /// <summary>Un evento concreto por su id, o null si ya no existe en el calendario.</summary>
    private async Task<Event?> ObtenerEventoAsync(string googleEventId, CancellationToken ct)
    {
        var servicio = await ObtenerServicioAsync();
        try
        {
            return await servicio.Events.Get("primary", googleEventId).ExecuteAsync(ct);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return null;
        }
    }
}
