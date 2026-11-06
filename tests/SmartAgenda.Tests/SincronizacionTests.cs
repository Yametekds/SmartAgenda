using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging.Abstractions;
using SmartAgenda.Models;
using SmartAgenda.Services;
using SmartAgenda.Services.Sincronizacion;

namespace SmartAgenda.Tests;

/// <summary>Calendario remoto en memoria: hace de Google Calendar compartido entre "dispositivos".</summary>
internal sealed class CalendarioFalso : ICalendarioRemoto
{
    public readonly Dictionary<string, EventoRemoto> Eventos = new();
    public bool Disponible { get; set; } = true;
    public bool SinRed { get; set; }
    public DateTime Reloj { get; set; } = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private int _siguienteId;

    private DateTime Tic() => Reloj = Reloj.AddSeconds(1);

    public Task<string> GuardarAsync(TareaItem tarea, CancellationToken ct = default)
    {
        if (SinRed) throw new HttpRequestException("sin red");
        var id = tarea.GoogleEventId is { } existente && Eventos.ContainsKey(existente) && !Eventos[existente].Cancelado
            ? existente
            : $"ev{++_siguienteId}";
        Eventos[id] = new EventoRemoto
        {
            Id = id, SmartagendaId = tarea.Id, Texto = tarea.Texto, Fecha = tarea.Fecha, Hora = tarea.Hora,
            Duracion = tarea.Duracion, Completada = tarea.Completada, Actualizado = Tic()
        };
        return Task.FromResult(id);
    }

    public Task EliminarAsync(string eventoId, CancellationToken ct = default)
    {
        if (SinRed) throw new HttpRequestException("sin red");
        if (Eventos.TryGetValue(eventoId, out var ev)) Eventos[eventoId] = ev with { Cancelado = true, Actualizado = Tic() };
        return Task.CompletedTask;
    }

    public Task<List<EventoRemoto>> ObtenerCambiosAsync(DateOnly desde, DateOnly hasta, DateTimeOffset? modificadosDesde, CancellationToken ct = default)
    {
        if (SinRed) throw new HttpRequestException("sin red");
        return Task.FromResult(Eventos.Values
            .Where(e => modificadosDesde is null || e.Actualizado >= modificadosDesde.Value.UtcDateTime)
            .Where(e => e.Cancelado || e.Fecha >= desde && e.Fecha < hasta)
            .ToList());
    }

    /// <summary>Simula un cambio hecho directamente en Google Calendar (web o app de Google).</summary>
    public void EditarEnGoogle(string id, Func<EventoRemoto, EventoRemoto> cambio) => Eventos[id] = cambio(Eventos[id]) with { Actualizado = Tic() };

    public string CrearEnGoogle(string texto, DateOnly fecha)
    {
        var id = $"ev{++_siguienteId}";
        Eventos[id] = new EventoRemoto { Id = id, Texto = texto, Fecha = fecha, Actualizado = Tic() };
        return id;
    }
}

/// <summary>Un "dispositivo": su propia carpeta de datos y su propio servicio de sincronización.</summary>
internal sealed class Dispositivo : IDisposable
{
    public readonly string Carpeta = Path.Combine(Path.GetTempPath(), "smartagenda-sync-" + Guid.NewGuid());
    public readonly TareaStorageService Storage;
    public readonly SincronizacionService Sync;

    public Dispositivo(CalendarioFalso calendario)
    {
        Storage = new TareaStorageService(Carpeta);
        Sync = new SincronizacionService(Storage, calendario, Carpeta, NullLogger<SincronizacionService>.Instance,
            () => new DateTimeOffset(calendario.Reloj));
    }

    public async Task<List<TareaItem>> Visibles() => (await Storage.ObtenerTodasAsync()).Where(t => !t.Eliminada).ToList();

    public void Dispose()
    {
        Sync.Dispose();
        if (Directory.Exists(Carpeta)) Directory.Delete(Carpeta, recursive: true);
    }
}

public sealed class SincronizacionTests : IDisposable
{
    private static readonly DateOnly Dia = new(2026, 11, 10);
    private readonly CalendarioFalso _google = new();
    private readonly Dispositivo _pc;
    private readonly Dispositivo _movil;

    public SincronizacionTests()
    {
        _pc = new Dispositivo(_google);
        _movil = new Dispositivo(_google);
    }

    public void Dispose()
    {
        _pc.Dispose();
        _movil.Dispose();
    }

    private static TareaItem Tarea(string texto, DateOnly? fecha = null) => new() { Texto = texto, Fecha = fecha ?? Dia, Hora = new TimeOnly(17, 30) };

    [Fact]
    public async Task Una_tarea_creada_en_el_movil_llega_al_pc()
    {
        var tarea = Tarea("Dentista");
        await _movil.Storage.AgregarTareaAsync(Dia, tarea);

        var enMovil = await _movil.Sync.SincronizarAsync();
        var enPc = await _pc.Sync.SincronizarAsync();

        Assert.Equal(1, enMovil.Enviadas);
        Assert.Equal(1, enPc.Nuevas);
        var copia = Assert.Single(await _pc.Visibles());
        Assert.Equal(tarea.Id, copia.Id);
        Assert.Equal("Dentista", copia.Texto);
        Assert.Equal(new TimeOnly(17, 30), copia.Hora);
        Assert.False(copia.PendienteSync);
        Assert.False(Assert.Single(await _movil.Visibles()).PendienteSync);
    }

    [Fact]
    public async Task Sin_conexion_se_guarda_y_se_envia_al_volver()
    {
        await _movil.Storage.AgregarTareaAsync(Dia, Tarea("Gimnasio"));
        _google.SinRed = true;

        var sinRed = await _movil.Sync.SincronizarAsync();
        Assert.NotNull(sinRed.Error);
        Assert.True(Assert.Single(await _movil.Visibles()).PendienteSync);

        _google.SinRed = false;
        var conRed = await _movil.Sync.SincronizarAsync();
        Assert.Equal(1, conRed.Enviadas);
        Assert.Single(_google.Eventos);
    }

    [Fact]
    public async Task Completar_en_un_dispositivo_se_ve_en_el_otro()
    {
        var tarea = Tarea("Llamar a mamá");
        await _pc.Storage.AgregarTareaAsync(Dia, tarea);
        await _pc.Sync.SincronizarAsync();
        await _movil.Sync.SincronizarAsync();

        var enMovil = Assert.Single(await _movil.Visibles());
        enMovil.Completada = true;
        await _movil.Storage.ActualizarTareaAsync(Dia, enMovil);
        await _movil.Sync.SincronizarAsync();
        var resultado = await _pc.Sync.SincronizarAsync();

        Assert.Equal(1, resultado.Actualizadas);
        Assert.True(Assert.Single(await _pc.Visibles()).Completada);
    }

    [Fact]
    public async Task Borrar_en_un_dispositivo_lo_borra_en_el_otro_y_no_reaparece()
    {
        await _pc.Storage.AgregarTareaAsync(Dia, Tarea("Reunión"));
        await _pc.Sync.SincronizarAsync();
        await _movil.Sync.SincronizarAsync();

        var enMovil = Assert.Single(await _movil.Visibles());
        await _movil.Storage.EliminarTareaAsync(Dia, enMovil.Id);
        Assert.Empty(await _movil.Storage.ObtenerTareasAsync(Dia));

        await _movil.Sync.SincronizarAsync();
        var resultado = await _pc.Sync.SincronizarAsync();

        Assert.Equal(1, resultado.Eliminadas);
        Assert.Empty(await _pc.Visibles());
        Assert.Empty(await _movil.Storage.ObtenerTodasAsync());

        await _movil.Sync.SincronizarAsync();
        Assert.Empty(await _movil.Visibles());
    }

    [Fact]
    public async Task Mover_de_dia_en_Google_mueve_la_tarea_aqui()
    {
        var tarea = Tarea("Cita médica");
        await _pc.Storage.AgregarTareaAsync(Dia, tarea);
        await _pc.Sync.SincronizarAsync();

        var otroDia = Dia.AddDays(2);
        _google.EditarEnGoogle(tarea.GoogleEventId ?? (await _pc.Visibles())[0].GoogleEventId!, e => e with { Fecha = otroDia, Texto = "Cita médica (cambiada)" });
        await _pc.Sync.SincronizarAsync();

        Assert.Empty(await _pc.Storage.ObtenerTareasAsync(Dia));
        Assert.Equal("Cita médica (cambiada)", Assert.Single(await _pc.Storage.ObtenerTareasAsync(otroDia)).Texto);
    }

    [Fact]
    public async Task Gana_el_cambio_mas_reciente()
    {
        var tarea = Tarea("Original");
        await _pc.Storage.AgregarTareaAsync(Dia, tarea);
        await _pc.Sync.SincronizarAsync();
        var eventoId = (await _pc.Visibles())[0].GoogleEventId!;

        // Primero se cambia en Google y después aquí, sin conexión: lo de aquí es más nuevo.
        _google.EditarEnGoogle(eventoId, e => e with { Texto = "Desde Google" });
        var local = (await _pc.Visibles())[0];
        _google.Reloj = _google.Reloj.AddMinutes(1);
        local.Texto = "Desde el PC";
        await _pc.Storage.ActualizarTareaAsync(Dia, local);
        local.ModificadaEn = _google.Reloj;
        await _pc.Storage.GuardarDesdeSincronizacionAsync(local, null);

        await _pc.Sync.SincronizarAsync();

        Assert.Equal("Desde el PC", (await _pc.Visibles())[0].Texto);
        Assert.Equal("Desde el PC", _google.Eventos[eventoId].Texto);
    }

    [Fact]
    public async Task Gana_Google_si_su_cambio_es_posterior()
    {
        var tarea = Tarea("Original");
        await _pc.Storage.AgregarTareaAsync(Dia, tarea);
        await _pc.Sync.SincronizarAsync();
        var eventoId = (await _pc.Visibles())[0].GoogleEventId!;

        var local = (await _pc.Visibles())[0];
        local.Texto = "Desde el PC";
        await _pc.Storage.ActualizarTareaAsync(Dia, local);
        local.ModificadaEn = _google.Reloj;
        await _pc.Storage.GuardarDesdeSincronizacionAsync(local, null);
        _google.Reloj = _google.Reloj.AddMinutes(1);
        _google.EditarEnGoogle(eventoId, e => e with { Texto = "Desde Google" });

        await _pc.Sync.SincronizarAsync();

        Assert.Equal("Desde Google", (await _pc.Visibles())[0].Texto);
        Assert.Equal("Desde Google", _google.Eventos[eventoId].Texto);
    }

    [Fact]
    public async Task Un_evento_creado_en_Google_se_importa_y_recibe_su_id()
    {
        var eventoId = _google.CrearEnGoogle("Cumpleaños de Ana", Dia);

        var resultado = await _pc.Sync.SincronizarAsync();

        Assert.Equal(1, resultado.Nuevas);
        var tarea = Assert.Single(await _pc.Visibles());
        Assert.Equal(eventoId, tarea.GoogleEventId);
        Assert.Equal(tarea.Id, _google.Eventos[eventoId].SmartagendaId);

        // El otro dispositivo la reconoce por ese id y no la duplica.
        await _movil.Sync.SincronizarAsync();
        Assert.Equal(tarea.Id, Assert.Single(await _movil.Visibles()).Id);
    }

    [Fact]
    public async Task Sin_cuenta_conectada_no_hace_nada()
    {
        _google.Disponible = false;
        await _pc.Storage.AgregarTareaAsync(Dia, Tarea("Solo aquí"));

        var resultado = await _pc.Sync.SincronizarAsync();

        Assert.True(resultado.SinConexion);
        Assert.Empty(_google.Eventos);
        Assert.Single(await _pc.Visibles());
    }

    [Fact]
    public async Task Las_tareas_antiguas_sin_enviar_se_suben_al_conectar()
    {
        // Tarea guardada antes de que existiera la sincronización: sin marca de pendiente.
        await _pc.Storage.GuardarDesdeSincronizacionAsync(new TareaItem { Texto = "De antes", Fecha = Dia }, null);

        var resultado = await _pc.Sync.SincronizarAsync();

        Assert.Equal(1, resultado.Enviadas);
        Assert.Single(_google.Eventos);
    }
}

public class LeerEventoGoogleTests
{
    [Fact]
    public void Lee_campos_privados_y_horario()
    {
        var id = Guid.NewGuid();
        var evento = new Event
        {
            Id = "abc",
            Summary = " Dentista ",
            Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 11, 10, 17, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 10))) },
            End = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 11, 10, 18, 15, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 10))) },
            ExtendedProperties = new Event.ExtendedPropertiesData
            {
                Private__ = new Dictionary<string, string> { ["smartagendaId"] = id.ToString(), ["completada"] = "true" }
            }
        };

        var leido = GoogleCalendarService.LeerEvento(evento)!;

        Assert.Equal(id, leido.SmartagendaId);
        Assert.True(leido.Completada);
        Assert.Equal("Dentista", leido.Texto);
        Assert.Equal(new DateOnly(2026, 11, 10), leido.Fecha);
        Assert.Equal(new TimeOnly(17, 30), leido.Hora);
        Assert.Equal(TimeSpan.FromMinutes(45), leido.Duracion);
    }

    [Fact]
    public void Evento_de_dia_completo_sin_campos_de_la_app()
    {
        var leido = GoogleCalendarService.LeerEvento(new Event { Id = "x", Summary = "Vacaciones", Start = new EventDateTime { Date = "2026-12-24" } })!;

        Assert.Null(leido.SmartagendaId);
        Assert.Null(leido.Completada);
        Assert.Null(leido.Hora);
        Assert.Equal(new DateOnly(2026, 12, 24), leido.Fecha);
    }

    [Fact]
    public void Evento_borrado()
    {
        var leido = GoogleCalendarService.LeerEvento(new Event { Id = "x", Status = "cancelled" })!;
        Assert.True(leido.Cancelado);
    }

    [Fact]
    public void Escribir_y_leer_la_misma_tarea_da_lo_mismo()
    {
        var tarea = new TareaItem { Texto = "Gimnasio", Fecha = new DateOnly(2026, 11, 12), Hora = new TimeOnly(9, 0), Duracion = TimeSpan.FromMinutes(90), Completada = true };
        var evento = new Event { Id = "e1" };

        GoogleCalendarService.AplicarTarea(evento, tarea, "Europe/Madrid");
        var leido = GoogleCalendarService.LeerEvento(evento)!;

        Assert.Equal(tarea.Id, leido.SmartagendaId);
        Assert.Equal(tarea.Fecha, leido.Fecha);
        Assert.Equal(tarea.Hora, leido.Hora);
        Assert.Equal(tarea.Duracion, leido.Duracion);
        Assert.True(leido.Completada);
    }
}
