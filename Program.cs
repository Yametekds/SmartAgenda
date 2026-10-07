using System.Security.Cryptography.X509Certificates;
using SmartAgenda.Components;
using SmartAgenda.Services;

var builder = WebApplication.CreateBuilder(args);

// Para usar la app desde el móvil (ver README), hace falta HTTPS con un certificado de
// confianza. Se prueban dos fuentes, en orden de preferencia:
//   1. Certificado real de Tailscale (Data/tailscale-cert.*) — de confianza automática,
//      sin instalar nada a mano en el móvil.
//   2. Certificado autofirmado para la red local (Data/dev-cert.pfx) — requiere instalarlo
//      manualmente como de confianza en el móvil.
// Ninguno de los dos se sube al repositorio.
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "Data");
var certTailscaleCrt = Path.Combine(dataDir, "tailscale-cert.crt");
var certTailscaleKey = Path.Combine(dataDir, "tailscale-cert.key");
var certLanPfx = Path.Combine(dataDir, "dev-cert.pfx");
var certLanPwd = Path.Combine(dataDir, "dev-cert.pwd");

X509Certificate2? CargarCertificadoHttps()
{
    if (File.Exists(certTailscaleCrt) && File.Exists(certTailscaleKey))
    {
        // En Windows, un certificado cargado desde PEM necesita reexportarse a PFX en memoria
        // para que Kestrel/SChannel pueda usar la clave privada correctamente.
        using var pem = X509Certificate2.CreateFromPemFile(certTailscaleCrt, certTailscaleKey);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), password: null);
    }

    if (File.Exists(certLanPfx) && File.Exists(certLanPwd))
    {
        var contraseña = File.ReadAllText(certLanPwd).Trim();
        return X509CertificateLoader.LoadPkcs12FromFile(certLanPfx, contraseña);
    }

    return null;
}

var certificadoHttps = CargarCertificadoHttps();
if (certificadoHttps is not null)
{
    builder.WebHost.ConfigureKestrel(opciones =>
    {
        opciones.ListenAnyIP(5203);
        opciones.ListenAnyIP(7094, listenOpciones => listenOpciones.UseHttps(certificadoHttps));
    });
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<TareaStorageService>();
builder.Services.AddSingleton<ParserLenguajeNaturalService>();
builder.Services.AddSingleton<GoogleCalendarService>();
builder.Services.AddSingleton<DeteccionVozService>();
builder.Services.AddSingleton<TranscripcionAudioService>();
builder.Services.AddScoped<LocalizationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Recibe un audio WAV grabado en el navegador y devuelve el texto transcrito con Whisper,
// ejecutado localmente en este servidor (sin API key ni servicios externos de pago).
app.MapPost("/api/transcribir", async (HttpRequest request, TranscripcionAudioService transcripcion) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    buffer.Position = 0;

    if (buffer.Length == 0)
    {
        return Results.BadRequest(new { error = "No se recibió audio." });
    }

    var idioma = request.Query["idioma"].ToString();

    try
    {
        var texto = await transcripcion.TranscribirAsync(buffer, idioma);
        return Results.Ok(new { texto });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
