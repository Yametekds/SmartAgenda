using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

public class GoogleCalendarService
{
    private readonly string _credencialesPath;
    private readonly string _tokenStorePath;
    private CalendarService? _servicio;

    public GoogleCalendarService(IWebHostEnvironment env, IConfiguration config)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _credencialesPath = config["GoogleCalendar:CredentialsPath"]
            ?? Path.Combine(dataDir, "credentials.json");
        _tokenStorePath = Path.Combine(dataDir, "google-token");
    }

    /// <summary>Hay un credentials.json propio del proyecto (requisito técnico de Google, se crea una sola vez).</summary>
    public bool EstaConfigurado => File.Exists(_credencialesPath);

    /// <summary>Ya se completó el inicio de sesión con Google y hay un token utilizable guardado localmente.</summary>
    public bool EstaConectado => _servicio != null || Directory.Exists(_tokenStorePath) && Directory.EnumerateFiles(_tokenStorePath).Any();

    /// <summary>
    /// Dispara el inicio de sesión explícito con Google (botón "Conectar con Google").
    /// Si ya hay un token válido guardado, no vuelve a pedir nada.
    /// </summary>
    public async Task ConectarAsync() => await ObtenerServicioAsync();

    /// <summary>Olvida la sesión guardada localmente. No afecta a los permisos en la cuenta de Google del usuario.</summary>
    public void Desconectar()
    {
        _servicio = null;
        if (Directory.Exists(_tokenStorePath))
        {
            Directory.Delete(_tokenStorePath, recursive: true);
        }
    }

    private async Task<CalendarService> ObtenerServicioAsync()
    {
        if (_servicio != null) return _servicio;

        if (!EstaConfigurado)
        {
            throw new InvalidOperationException(
                $"No se encontró el archivo de credenciales de Google en '{_credencialesPath}'. " +
                "Sigue las instrucciones del README para crear tu proyecto OAuth en Google Cloud Console (solo hay que hacerlo una vez).");
        }

        UserCredential credencial;
        await using (var stream = new FileStream(_credencialesPath, FileMode.Open, FileAccess.Read))
        {
            credencial = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { CalendarService.Scope.Calendar },
                "usuario-local",
                CancellationToken.None,
                new FileDataStore(_tokenStorePath, true));
        }

        _servicio = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credencial,
            ApplicationName = "Tareas Semanales"
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
        solicitud.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        var resultado = await solicitud.ExecuteAsync();
        return resultado.Items?.ToList() ?? new List<Event>();
    }
}
