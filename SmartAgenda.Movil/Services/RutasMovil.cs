using SmartAgenda.Services;

namespace SmartAgenda.Movil.Services;

/// <summary>En Android los datos viven en la carpeta privada de la app (se borran al desinstalarla).</summary>
public class RutasMovil : IRutasApp
{
    public string CarpetaDatos { get; } = Path.Combine(FileSystem.AppDataDirectory, "Data");
}
