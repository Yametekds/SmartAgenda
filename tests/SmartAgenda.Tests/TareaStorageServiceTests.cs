using SmartAgenda.Models;
using SmartAgenda.Services;

namespace SmartAgenda.Tests;

public sealed class TareaStorageServiceTests : IDisposable
{
    private static readonly DateOnly Dia = new(2026, 10, 28);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "smartagenda-tests-" + Guid.NewGuid());
    private readonly TareaStorageService _storage;

    public TareaStorageServiceTests()
    {
        _storage = new TareaStorageService(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TareaItem Tarea(string texto, DateOnly fecha) => new() { Texto = texto, Fecha = fecha };

    [Fact]
    public async Task Agregar_actualizar_y_eliminar()
    {
        var tarea = Tarea("Dentista", Dia);
        await _storage.AgregarTareaAsync(Dia, tarea);

        tarea.Completada = true;
        await _storage.ActualizarTareaAsync(Dia, tarea);
        var leidas = await _storage.ObtenerTareasAsync(Dia);
        Assert.True(Assert.Single(leidas).Completada);

        await _storage.EliminarTareaAsync(Dia, tarea.Id);
        Assert.Empty(await _storage.ObtenerTareasAsync(Dia));
    }

    [Fact]
    public async Task Altas_simultaneas_no_se_pierden()
    {
        var altas = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => _storage.AgregarTareaAsync(Dia, Tarea($"Tarea {i}", Dia))));
        await Task.WhenAll(altas);

        Assert.Equal(50, (await _storage.ObtenerTareasAsync(Dia)).Count);
    }

    [Fact]
    public async Task Mover_de_dia_deja_la_tarea_solo_en_el_destino()
    {
        var tarea = Tarea("Gimnasio", Dia);
        await _storage.AgregarTareaAsync(Dia, tarea);

        var destino = Dia.AddDays(9);
        tarea.Fecha = destino;
        await _storage.MoverTareaAsync(Dia, tarea);

        Assert.Empty(await _storage.ObtenerTareasAsync(Dia));
        Assert.Equal(tarea.Id, Assert.Single(await _storage.ObtenerTareasAsync(destino)).Id);
    }

    [Fact]
    public async Task Archivo_danado_se_aparta_sin_romper_la_lectura()
    {
        var ruta = Path.Combine(_dir, "tareas", $"{Dia:yyyy-MM-dd}.json");
        await File.WriteAllTextAsync(ruta, "{ esto no es json");

        Assert.Empty(await _storage.ObtenerTareasAsync(Dia));
        Assert.False(File.Exists(ruta));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "tareas"), "*.danado-*"));
    }

    [Fact]
    public async Task No_quedan_archivos_temporales()
    {
        await _storage.AgregarTareaAsync(Dia, Tarea("Algo", Dia));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tareas"), "*.tmp"));
    }

    [Fact]
    public async Task Nombre_de_semana()
    {
        await _storage.GuardarNombreSemanaAsync(Dia, "  Exámenes ");
        Assert.Equal("Exámenes", await _storage.ObtenerNombreSemanaAsync(Dia));

        await _storage.GuardarNombreSemanaAsync(Dia, "");
        Assert.Null(await _storage.ObtenerNombreSemanaAsync(Dia));
    }
}
