using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
builder.Services.AddSingleton<AccesoService>();

// La sesión de acceso es una cookie firmada con estas claves; se guardan en Data/ para que la
// sesión de cada dispositivo sobreviva a reinicios de la app (y viajen con ella si se mueve de equipo).
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "claves-proteccion")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opciones =>
    {
        opciones.Cookie.Name = "smartagenda_acceso";
        opciones.Cookie.HttpOnly = true;
        opciones.Cookie.SameSite = SameSiteMode.Lax;
        opciones.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opciones.ExpireTimeSpan = TimeSpan.FromDays(180);
        opciones.SlidingExpiration = true;
        opciones.LoginPath = AccesoService.RutaAcceso;
    });

// Freno a quien intente adivinar la contraseña: 5 intentos por minuto y por IP.
builder.Services.AddRateLimiter(opciones =>
{
    opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opciones.AddPolicy("acceso", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Se crea ya para que, si es la primera vez, la contraseña generada salga en la consola al arrancar.
var acceso = app.Services.GetRequiredService<AccesoService>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();

// Todo (páginas, la conexión de Blazor, la API) exige haber entrado con la contraseña, salvo la
// propia página de acceso y, si así está configurado, las peticiones desde el mismo equipo.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true ||
        (acceso.PermitirLocalSinContrasena && AccesoService.EsLocal(ctx)) ||
        ctx.Request.Path.StartsWithSegments(AccesoService.RutaAcceso))
    {
        await next();
        return;
    }

    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var volver = ctx.Request.Path + ctx.Request.QueryString;
    ctx.Response.Redirect($"{AccesoService.RutaAcceso}?volver={Uri.EscapeDataString(volver)}");
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Recibe un audio WAV grabado en el navegador y devuelve el texto transcrito con Whisper,
// ejecutado localmente en este servidor (sin API key ni servicios externos de pago).
static bool PrefiereIngles(HttpRequest request) =>
    !request.Headers.AcceptLanguage.ToString().TrimStart().StartsWith("es", StringComparison.OrdinalIgnoreCase);

app.MapGet(AccesoService.RutaAcceso, (HttpContext ctx, string? volver) =>
    Results.Content(AccesoService.PaginaAcceso(PrefiereIngles(ctx.Request), AccesoService.RutaDeVueltaSegura(volver), error: false), "text/html; charset=utf-8"));

app.MapPost(AccesoService.RutaAcceso, async (HttpContext ctx) =>
{
    var formulario = await ctx.Request.ReadFormAsync();
    var volver = AccesoService.RutaDeVueltaSegura(formulario["volver"]);

    if (!acceso.EsCorrecta(formulario["contrasena"]))
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        return Results.Content(AccesoService.PaginaAcceso(PrefiereIngles(ctx.Request), volver, error: true), "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
    }

    var identidad = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "propietario") }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identidad), new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect(volver);
}).DisableAntiforgery().RequireRateLimiting("acceso");

// Inicio de sesión con Google en el navegador del usuario (funciona igual desde el móvil que desde el PC).
// La dirección de vuelta se construye con la misma dirección por la que se está usando la app; tiene
// que estar dada de alta en las credenciales de Google Cloud (ver README).
const string CookieEstadoGoogle = "smartagenda_google_estado";
static string RedirectUriGoogle(HttpRequest request) => $"{request.Scheme}://{request.Host}{GoogleCalendarService.RutaCallback}";

app.MapGet(GoogleCalendarService.RutaConectar, (HttpContext ctx, GoogleCalendarService google) =>
{
    if (!google.EstaConfigurado) return Results.Redirect("/?google=error");

    // "state" aleatorio que Google nos devuelve tal cual: comprueba que la vuelta viene de un
    // inicio de sesión que empezó aquí y no de un enlace preparado por otro.
    var estado = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    ctx.Response.Cookies.Append(CookieEstadoGoogle, estado, new CookieOptions
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10)
    });
    return Results.Redirect(google.CrearUrlAutorizacion(RedirectUriGoogle(ctx.Request), estado));
});

app.MapGet(GoogleCalendarService.RutaCallback, async (HttpContext ctx, GoogleCalendarService google, ILogger<Program> logger, string? code, string? state, string? error) =>
{
    var estadoEsperado = ctx.Request.Cookies[CookieEstadoGoogle];
    ctx.Response.Cookies.Delete(CookieEstadoGoogle);

    if (error is not null || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(estadoEsperado) || state != estadoEsperado)
    {
        logger.LogWarning("Vuelta de Google rechazada (error: {Error}, state válido: {StateOk}).", error, state == estadoEsperado);
        return Results.Redirect("/?google=error");
    }

    try
    {
        await google.CompletarAutorizacionAsync(code, RedirectUriGoogle(ctx.Request), ctx.RequestAborted);
        return Results.Redirect("/?google=ok");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "No se pudo canjear el código de Google por un token.");
        return Results.Redirect("/?google=error");
    }
});

app.MapPost("/api/transcribir", async (HttpRequest request, TranscripcionAudioService transcripcion, ILogger<Program> logger) =>
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
        // El detalle se queda en el log del servidor; al navegador solo le llega que falló.
        logger.LogError(ex, "Error al transcribir el audio.");
        return Results.Problem("No se pudo transcribir el audio.");
    }
});

app.Run();
