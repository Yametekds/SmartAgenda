using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartAgenda.Models;

namespace SmartAgenda.Services;

/// <summary>
/// Persistencia en JSON, un archivo por fecha. Toda operación de leer-modificar-escribir se hace
/// entera dentro del mismo cerrojo (si no, dos cambios a la vez —dos pestañas, el móvil y el PC—
/// podían leer la misma lista y el segundo en guardar borraba lo que añadió el primero), y cada
/// escritura va primero a un archivo temporal que luego se renombra, para que un corte a mitad de
/// escritura nunca deje el archivo del día a medias.
/// </summary>
public class TareaStorageService
{
    private static readonly JsonSerializerOptions OpcionesJson = new() { WriteIndented = true };

    private readonly string _dataDir;
    private readonly string _rutaSemanas;
    private readonly ILogger<TareaStorageService>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TareaStorageService(IRutasApp rutas, ILogger<TareaStorageService> logger)
        : this(rutas.CarpetaDatos, logger)
    {
    }

    /// <param name="dataRoot">Carpeta "Data" donde se guardan las tareas y los nombres de semana.</param>
    public TareaStorageService(string dataRoot, ILogger<TareaStorageService>? logger = null)
    {
        _dataDir = Path.Combine(dataRoot, "tareas");
        Directory.CreateDirectory(_dataDir);
        _rutaSemanas = Path.Combine(dataRoot, "semanas.json");
        _logger = logger;
    }

    private string RutaFecha(DateOnly fecha) => Path.Combine(_dataDir, $"{fecha:yyyy-MM-dd}.json");

    /// <summary>Las tareas visibles de un día (sin las borradas que aún esperan a llegar a Google).</summary>
    public async Task<List<TareaItem>> ObtenerTareasAsync(DateOnly fecha)
    {
        await _lock.WaitAsync();
        try
        {
            var tareas = await LeerAsync<List<TareaItem>>(RutaFecha(fecha)) ?? new List<TareaItem>();
            tareas.RemoveAll(t => t.Eliminada);
            return tareas;
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

    // Los cambios hechos por el usuario quedan marcados como pendientes de enviar a Google y con
    // la hora del cambio, para que la sincronización sepa qué versión es la más reciente.
    private static void MarcarCambioLocal(TareaItem tarea)
    {
        tarea.ModificadaEn = DateTime.UtcNow;
        tarea.PendienteSync = true;
    }

    public Task AgregarTareaAsync(DateOnly fecha, TareaItem tarea)
    {
        MarcarCambioLocal(tarea);
        return ModificarDiaAsync(fecha, tareas => tareas.Add(tarea));
    }

    public Task ActualizarTareaAsync(DateOnly fecha, TareaItem tarea)
    {
        MarcarCambioLocal(tarea);
        return ModificarDiaAsync(fecha, tareas =>
        {
            var idx = tareas.FindIndex(t => t.Id == tarea.Id);
            if (idx >= 0) tareas[idx] = tarea;
        });
    }

    /// <summary>
    /// Borra la tarea. Si ya estaba en Google, se queda marcada como borrada hasta que la
    /// sincronización borre también el evento; si no, se quita directamente.
    /// </summary>
    public Task EliminarTareaAsync(DateOnly fecha, Guid id) =>
        ModificarDiaAsync(fecha, tareas =>
        {
            var tarea = tareas.FirstOrDefault(t => t.Id == id);
            if (tarea is null) return;

            if (string.IsNullOrEmpty(tarea.GoogleEventId))
            {
                tareas.Remove(tarea);
                return;
            }

            tarea.Eliminada = true;
            MarcarCambioLocal(tarea);
        });

    /// <summary>Quita la tarea del todo (cuando Google ya confirmó el borrado).</summary>
    public Task PurgarTareaAsync(DateOnly fecha, Guid id) =>
        ModificarDiaAsync(fecha, tareas => tareas.RemoveAll(t => t.Id == id));

    /// <summary>
    /// Anota que la tarea ya está en Google con <paramref name="googleEventId"/>. Solo la da por
    /// sincronizada si no ha vuelto a cambiar mientras se enviaba (<paramref name="modificadaEnEnviada"/>).
    /// </summary>
    public Task MarcarSincronizadaAsync(DateOnly fecha, Guid id, string googleEventId, DateTime modificadaEnEnviada) =>
        ModificarDiaAsync(fecha, tareas =>
        {
            var tarea = tareas.FirstOrDefault(t => t.Id == id);
            if (tarea is null) return;

            tarea.GoogleEventId = googleEventId;
            if (tarea.ModificadaEn == modificadaEnEnviada) tarea.PendienteSync = false;
        });

    /// <summary>
    /// Guarda una tarea tal como llega de la sincronización, sin marcarla como cambio local. Si
    /// cambió de día, la pasa de <paramref name="fechaAnterior"/> a <c>tarea.Fecha</c>.
    /// </summary>
    public async Task GuardarDesdeSincronizacionAsync(TareaItem tarea, DateOnly? fechaAnterior)
    {
        await _lock.WaitAsync();
        try
        {
            if (fechaAnterior is { } anterior && anterior != tarea.Fecha)
            {
                var origen = await LeerAsync<List<TareaItem>>(RutaFecha(anterior)) ?? new List<TareaItem>();
                origen.RemoveAll(t => t.Id == tarea.Id);
                await EscribirAsync(RutaFecha(anterior), origen);
            }

            var destino = await LeerAsync<List<TareaItem>>(RutaFecha(tarea.Fecha)) ?? new List<TareaItem>();
            var idx = destino.FindIndex(t => t.Id == tarea.Id);
            if (idx >= 0) destino[idx] = tarea; else destino.Add(tarea);
            await EscribirAsync(RutaFecha(tarea.Fecha), destino);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Todas las tareas guardadas, incluidas las borradas pendientes de llegar a Google.</summary>
    public async Task<List<TareaItem>> ObtenerTodasAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var todas = new List<TareaItem>();
            foreach (var ruta in Directory.EnumerateFiles(_dataDir, "????-??-??.json"))
            {
                if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(ruta), "yyyy-MM-dd", out var fecha)) continue;
                foreach (var tarea in await LeerAsync<List<TareaItem>>(ruta) ?? new List<TareaItem>())
                {
                    tarea.Fecha = fecha;
                    todas.Add(tarea);
                }
            }
            return todas;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Pasa la tarea de <paramref name="fechaOrigen"/> a <c>tarea.Fecha</c> en una sola operación, para
    /// que nunca quede ni duplicada ni perdida si algo más escribe a la vez.
    /// </summary>
    public async Task MoverTareaAsync(DateOnly fechaOrigen, TareaItem tarea)
    {
        MarcarCambioLocal(tarea);
        if (fechaOrigen == tarea.Fecha)
        {
            await ActualizarTareaAsync(fechaOrigen, tarea);
            return;
        }

        await _lock.WaitAsync();
        try
        {
            var origen = await LeerAsync<List<TareaItem>>(RutaFecha(fechaOrigen)) ?? new List<TareaItem>();
            var destino = await LeerAsync<List<TareaItem>>(RutaFecha(tarea.Fecha)) ?? new List<TareaItem>();

            origen.RemoveAll(t => t.Id == tarea.Id);
            destino.RemoveAll(t => t.Id == tarea.Id);
            destino.Add(tarea);

            // Primero el destino: si se cortara entre las dos escrituras, la tarea quedaría repetida
            // (molesto, pero visible y fácil de arreglar) en vez de perdida.
            await EscribirAsync(RutaFecha(tarea.Fecha), destino);
            await EscribirAsync(RutaFecha(fechaOrigen), origen);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ModificarDiaAsync(DateOnly fecha, Action<List<TareaItem>> cambio)
    {
        await _lock.WaitAsync();
        try
        {
            var ruta = RutaFecha(fecha);
            var tareas = await LeerAsync<List<TareaItem>>(ruta) ?? new List<TareaItem>();
            cambio(tareas);
            await EscribirAsync(ruta, tareas);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Nombre personalizado de la semana que empieza en <paramref name="lunes"/>, o null si no se ha puesto ninguno.</summary>
    public async Task<string?> ObtenerNombreSemanaAsync(DateOnly lunes)
    {
        await _lock.WaitAsync();
        try
        {
            var nombres = await LeerAsync<Dictionary<string, string>>(_rutaSemanas) ?? new Dictionary<string, string>();
            return nombres.TryGetValue(lunes.ToString("yyyy-MM-dd"), out var nombre) && !string.IsNullOrWhiteSpace(nombre)
                ? nombre
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task GuardarNombreSemanaAsync(DateOnly lunes, string? nombre)
    {
        await _lock.WaitAsync();
        try
        {
            var nombres = await LeerAsync<Dictionary<string, string>>(_rutaSemanas) ?? new Dictionary<string, string>();

            var clave = lunes.ToString("yyyy-MM-dd");
            if (string.IsNullOrWhiteSpace(nombre))
            {
                nombres.Remove(clave);
            }
            else
            {
                nombres[clave] = nombre.Trim();
            }

            await EscribirAsync(_rutaSemanas, nombres);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- Solo se llaman con el cerrojo ya tomado ----

    private async Task<T?> LeerAsync<T>(string ruta) where T : class
    {
        if (!File.Exists(ruta)) return null;

        var json = await File.ReadAllTextAsync(ruta);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            // Un archivo dañado no debe tumbar la app: se aparta con otro nombre (para poder
            // recuperarlo a mano) y se sigue como si ese día estuviera vacío.
            var apartado = $"{ruta}.danado-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(ruta, apartado, overwrite: true);
            _logger?.LogError(ex, "El archivo {Ruta} no es un JSON válido; se ha apartado como {Apartado}.", ruta, apartado);
            return null;
        }
    }

    private static async Task EscribirAsync<T>(string ruta, T datos)
    {
        var temporal = ruta + ".tmp";
        await File.WriteAllTextAsync(temporal, JsonSerializer.Serialize(datos, OpcionesJson));
        File.Move(temporal, ruta, overwrite: true);
    }
}
