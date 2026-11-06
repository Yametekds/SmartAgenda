using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartAgenda.Movil.Services;
using SmartAgenda.Services;
using SmartAgenda.Services.Sincronizacion;

namespace SmartAgenda.Movil;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddSingleton<IRutasApp, RutasMovil>();
        builder.Services.AddSingleton<IConexionGoogle, ConexionGoogleMovil>();

        // En el móvil, la decodificación rápida (greedy): turbo sigue siendo mucho más preciso que
        // el modelo anterior y así cada frase tarda bastante menos en transcribirse.
        builder.Services.Configure<OpcionesVoz>(opciones => opciones.Decodificacion = EstrategiaDecodificacion.Greedy);

        builder.Services.AddSingleton<TareaStorageService>();
        builder.Services.AddSingleton<ParserLenguajeNaturalService>();
        builder.Services.AddSingleton<GoogleCalendarService>();
        builder.Services.AddSingleton<ICalendarioRemoto>(sp => sp.GetRequiredService<GoogleCalendarService>());
        builder.Services.AddSingleton<SincronizacionService>();
        builder.Services.AddSingleton<DeteccionVozService>();
        builder.Services.AddSingleton<TranscripcionAudioService>();
        builder.Services.AddScoped<LocalizationService>();

        var app = builder.Build();

        // Sincroniza con Google Calendar cada 5 minutos mientras la app esté abierta (y además al
        // abrirla y tras cada cambio).
        app.Services.GetRequiredService<SincronizacionService>().IniciarPeriodica(TimeSpan.FromMinutes(5));

        // El modelo de voz (~574 MB) solo se descarga solo si hay Wi-Fi; con datos móviles se
        // descargará la primera vez que se use el dictado.
        if (Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi))
        {
            var transcripcion = app.Services.GetRequiredService<TranscripcionAudioService>();
            _ = Task.Run(async () =>
            {
                try { await transcripcion.PrepararAsync(); }
                catch (Exception ex) { app.Services.GetRequiredService<ILogger<MauiApp>>().LogWarning(ex, "No se pudo preparar el modelo de voz."); }
            });
        }

        return app;
    }
}
