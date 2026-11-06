namespace SmartAgenda.Services;

/// <summary>En el PC los datos viven en la carpeta Data/ junto a la app, como hasta ahora.</summary>
public class RutasWeb(IWebHostEnvironment env) : IRutasApp
{
    public string CarpetaDatos { get; } = Path.Combine(env.ContentRootPath, "Data");
}
