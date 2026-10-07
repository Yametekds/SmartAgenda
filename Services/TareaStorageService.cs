using System.Text.Json;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

public class TareaStorageService
{
    private readonly string _dataDir;
    private readonly string _rutaSemanas;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TareaStorageService(IWebHostEnvironment env)
    {
        _dataDir = Path.Combine(env.ContentRootPath, "Data", "tareas");
        Directory.CreateDirectory(_dataDir);
        _rutaSemanas = Path.Combine(env.ContentRootPath, "Data", "semanas.json");
    }

    private string RutaFecha(DateOnly fecha) => Path.Combine(_dataDir, $"{fecha:yyyy-MM-dd}.json");

    public async Task<List<TareaItem>> ObtenerTareasAsync(DateOnly fecha)
    {
        var ruta = RutaFecha(fecha);
        if (!File.Exists(ruta)) return new List<TareaItem>();

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(ruta);
            if (string.IsNullOrWhiteSpace(json)) return new List<TareaItem>();
            return JsonSerializer.Deserialize<List<TareaItem>>(json) ?? new List<TareaItem>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Las tareas de los 7 días que empiezan en <paramref name="lunes"/>, indexadas por fecha.</summary>
    public async Task<Dictionary<DateOnly, List<TareaItem>>> ObtenerSemanaAsync(DateOnly lunes)
    {
        var resultado = new Dictionary<DateOnly, List<TareaItem>>();
        for (var i = 0; i < 7; i++)
        {
            var fecha = lunes.AddDays(i);
            resultado[fecha] = await ObtenerTareasAsync(fecha);
        }
        return resultado;
    }

    public async Task AgregarTareaAsync(DateOnly fecha, TareaItem tarea)
    {
        var tareas = await ObtenerTareasAsync(fecha);
        tareas.Add(tarea);
        await GuardarAsync(fecha, tareas);
    }

    public async Task ActualizarTareaAsync(DateOnly fecha, TareaItem tarea)
    {
        var tareas = await ObtenerTareasAsync(fecha);
        var idx = tareas.FindIndex(t => t.Id == tarea.Id);
        if (idx >= 0) tareas[idx] = tarea;
        await GuardarAsync(fecha, tareas);
    }

    public async Task EliminarTareaAsync(DateOnly fecha, Guid id)
    {
        var tareas = await ObtenerTareasAsync(fecha);
        tareas.RemoveAll(t => t.Id == id);
        await GuardarAsync(fecha, tareas);
    }

    private async Task GuardarAsync(DateOnly fecha, List<TareaItem> tareas)
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(tareas, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(RutaFecha(fecha), json);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Nombre personalizado de la semana que empieza en <paramref name="lunes"/>, o null si no se ha puesto ninguno.</summary>
    public async Task<string?> ObtenerNombreSemanaAsync(DateOnly lunes)
    {
        var nombres = await LeerNombresSemanaAsync();
        return nombres.TryGetValue(lunes.ToString("yyyy-MM-dd"), out var nombre) && !string.IsNullOrWhiteSpace(nombre)
            ? nombre
            : null;
    }

    public async Task GuardarNombreSemanaAsync(DateOnly lunes, string? nombre)
    {
        await _lock.WaitAsync();
        try
        {
            var nombres = File.Exists(_rutaSemanas)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_rutaSemanas)) ?? new()
                : new Dictionary<string, string>();

            var clave = lunes.ToString("yyyy-MM-dd");
            if (string.IsNullOrWhiteSpace(nombre))
            {
                nombres.Remove(clave);
            }
            else
            {
                nombres[clave] = nombre.Trim();
            }

            var json = JsonSerializer.Serialize(nombres, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_rutaSemanas, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, string>> LeerNombresSemanaAsync()
    {
        if (!File.Exists(_rutaSemanas)) return new Dictionary<string, string>();

        await _lock.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(_rutaSemanas);
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        finally
        {
            _lock.Release();
        }
    }
}
