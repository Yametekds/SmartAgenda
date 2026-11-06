using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartAgenda.Models;

namespace SmartAgenda.Services.Sincronizacion;

/// <summary>
/// Sincroniza las tareas de este dispositivo con el calendario compartido (Google Calendar).
/// Cada dispositivo trabaja en local, sin conexión si hace falta, y al sincronizar:
/// <list type="number">
/// <item>Trae los eventos que cambiaron desde la última vez y los aplica aquí, salvo que la
/// versión de aquí sea más reciente.</item>
/// <item>Envía los cambios de aquí pendientes: altas, ediciones y borrados.</item>
/// </list>
/// Si la misma tarea cambió en dos sitios, gana el cambio más reciente.
/// </summary>
public class SincronizacionService : IDisposable
{
    /// <summary>Ventana de fechas que se sincroniza: desde unas semanas atrás hasta un año vista.</summary>
    public static readonly int DiasAtras = 60;
    public static readonly int DiasAdelante = 400;

    // Margen al guardar la hora de la última sincronización, por si los relojes de este equipo y
    // de Google no coinciden del todo. Volver a traer un evento ya visto no cambia nada.
    private static readonly TimeSpan MargenReloj = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EsperaTrasCambio = TimeSpan.FromSeconds(3);

    private readonly TareaStorageService _storage;
    private readonly ICalendarioRemoto _remoto;
    private readonly ILogger<SincronizacionService> _logger;
    private readonly string _rutaEstado;
    private readonly SemaphoreSlim _enCurso = new(1, 1);
    private readonly Func<DateTimeOffset> _ahora;
    private Timer? _periodica;
    private CancellationTokenSource? _esperaCambio;

    public SincronizacionService(TareaStorageService storage, ICalendarioRemoto remoto, IRutasApp rutas, ILogger<SincronizacionService> logger)
        : this(storage, remoto, rutas.CarpetaDatos, logger, () => DateTimeOffset.UtcNow)
    {
    }

    public SincronizacionService(TareaStorageService storage, ICalendarioRemoto remoto, string carpetaDatos, ILogger<SincronizacionService> logger, Func<DateTimeOffset> ahora)
    {
        _storage = storage;
        _remoto = remoto;
        _logger = logger;
        _ahora = ahora;
        Directory.CreateDirectory(carpetaDatos);
        _rutaEstado = Path.Combine(carpetaDatos, "sincronizacion.json");
    }

    /// <summary>Se lanza cuando la sincronización cambió tareas guardadas aquí, para refrescar la vista.</summary>
    public event Action? OnCambios;

    public bool Sincronizando => _enCurso.CurrentCount == 0;

    /// <summary>Sincroniza cada <paramref name="intervalo"/> mientras la app esté abierta.</summary>
    public void IniciarPeriodica(TimeSpan intervalo)
    {
        _periodica?.Dispose();
        _periodica = new Timer(_ => _ = SincronizarEnSegundoPlanoAsync(), null, TimeSpan.Zero, intervalo);
    }

    /// <summary>
    /// Pide una sincronización tras un cambio local. Espera unos segundos por si llegan más cambios
    /// seguidos, y así los envía todos juntos.
    /// </summary>
    public void SolicitarSincronizacion()
    {
        if (!_remoto.Disponible) return;

        var anterior = Interlocked.Exchange(ref _esperaCambio, new CancellationTokenSource());
        anterior?.Cancel();
        var token = _esperaCambio!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EsperaTrasCambio, token);
                await SincronizarEnSegundoPlanoAsync();
            }
            catch (OperationCanceledException)
            {
                // Llegó otro cambio: lo enviará la siguiente espera.
            }
        });
    }

    private async Task SincronizarEnSegundoPlanoAsync()
    {
        try
        {
            await SincronizarAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falló la sincronización en segundo plano; se reintentará más tarde.");
        }
    }

    public async Task<ResultadoSincronizacion> SincronizarAsync(CancellationToken ct = default)
    {
        if (!_remoto.Disponible) return ResultadoSincronizacion.NoConectado;

        await _enCurso.WaitAsync(ct);
        try
        {
            var hoy = DateOnly.FromDateTime(_ahora().LocalDateTime);
            var desde = hoy.AddDays(-DiasAtras);
            var hasta = hoy.AddDays(DiasAdelante);

            int nuevas = 0, actualizadas = 0, eliminadas = 0;
            var estado = await LeerEstadoAsync();
            var inicioConsulta = _ahora();

            // 1. Traer lo que cambió en el calendario.
            List<EventoRemoto> eventos;
            try
            {
                eventos = await _remoto.ObtenerCambiosAsync(desde, hasta, estado.UltimaSincronizacion, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "No se pudieron traer los cambios del calendario.");
                return new ResultadoSincronizacion { Error = ex.Message };
            }

            var locales = await _storage.ObtenerTodasAsync();
            var porId = locales.ToDictionary(t => t.Id);
            var porEvento = locales.Where(t => !string.IsNullOrEmpty(t.GoogleEventId))
                .GroupBy(t => t.GoogleEventId!).ToDictionary(g => g.Key, g => g.First());

            foreach (var evento in eventos)
            {
                TareaItem? local = null;
                if (evento.SmartagendaId is { } id) porId.TryGetValue(id, out local);
                if (local is null) porEvento.TryGetValue(evento.Id, out local);

                switch (await AplicarAsync(evento, local))
                {
                    case Cambio.Nueva: nuevas++; break;
                    case Cambio.Actualizada: actualizadas++; break;
                    case Cambio.Eliminada: eliminadas++; break;
                }
            }

            await GuardarEstadoAsync(new EstadoSincronizacion { UltimaSincronizacion = inicioConsulta - MargenReloj });

            // 2. Enviar lo pendiente de aquí.
            var (enviadas, errores) = await EnviarPendientesAsync(desde, ct);

            var resultado = new ResultadoSincronizacion
            {
                Nuevas = nuevas,
                Actualizadas = actualizadas,
                Eliminadas = eliminadas,
                Enviadas = enviadas,
                Errores = errores
            };

            if (resultado.HuboCambiosLocales || enviadas > 0) OnCambios?.Invoke();
            _logger.LogInformation("Sincronización: {Nuevas} nuevas, {Actualizadas} actualizadas, {Eliminadas} borradas, {Enviadas} enviadas, {Errores} con error.",
                nuevas, actualizadas, eliminadas, enviadas, errores);
            return resultado;
        }
        finally
        {
            _enCurso.Release();
        }
    }

    private enum Cambio { Ninguno, Nueva, Actualizada, Eliminada }

    private async Task<Cambio> AplicarAsync(EventoRemoto evento, TareaItem? local)
    {
        var localMasReciente = local is { PendienteSync: true } && local.ModificadaEn > evento.Actualizado;

        if (evento.Cancelado)
        {
            if (local is null || local.Eliminada && !localMasReciente)
            {
                if (local is not null) await _storage.PurgarTareaAsync(local.Fecha, local.Id);
                return Cambio.Ninguno;
            }

            if (localMasReciente)
            {
                // Se editó aquí después de borrarse en otro sitio: se conserva y se vuelve a crear.
                local.GoogleEventId = null;
                await _storage.GuardarDesdeSincronizacionAsync(local, local.Fecha);
                return Cambio.Ninguno;
            }

            await _storage.PurgarTareaAsync(local.Fecha, local.Id);
            return Cambio.Eliminada;
        }

        if (local is not null)
        {
            // Lo de aquí es más nuevo (o está borrado aquí y aún sin enviar): se enviará después.
            if (localMasReciente || local.Eliminada) return Cambio.Ninguno;

            var fechaAnterior = local.Fecha;
            var cambia = local.Texto != evento.Texto || local.Fecha != evento.Fecha || local.Hora != evento.Hora ||
                         local.Duracion != evento.Duracion || evento.Completada is { } c && c != local.Completada;

            local.Texto = evento.Texto;
            local.Fecha = evento.Fecha;
            local.Hora = evento.Hora;
            local.Duracion = evento.Duracion;
            if (evento.Completada is { } completada) local.Completada = completada;
            local.GoogleEventId = evento.Id;
            local.ModificadaEn = evento.Actualizado;
            // Un evento creado fuera de la app aún no lleva el id de la tarea: se le añade al enviarlo.
            local.PendienteSync = evento.SmartagendaId is null;

            await _storage.GuardarDesdeSincronizacionAsync(local, fechaAnterior);
            return cambia ? Cambio.Actualizada : Cambio.Ninguno;
        }

        await _storage.GuardarDesdeSincronizacionAsync(new TareaItem
        {
            Id = evento.SmartagendaId ?? Guid.NewGuid(),
            Texto = evento.Texto,
            Fecha = evento.Fecha,
            Hora = evento.Hora,
            Duracion = evento.Duracion,
            Completada = evento.Completada ?? false,
            GoogleEventId = evento.Id,
            ModificadaEn = evento.Actualizado,
            PendienteSync = evento.SmartagendaId is null
        }, null);
        return Cambio.Nueva;
    }

    private async Task<(int Enviadas, int Errores)> EnviarPendientesAsync(DateOnly desde, CancellationToken ct)
    {
        int enviadas = 0, errores = 0;

        // También las tareas de antes de que existiera la sincronización que nunca llegaron a
        // Google, siempre que no sean demasiado antiguas.
        var pendientes = (await _storage.ObtenerTodasAsync())
            .Where(t => t.PendienteSync || string.IsNullOrEmpty(t.GoogleEventId) && !t.Eliminada && t.Fecha >= desde);

        foreach (var tarea in pendientes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (tarea.Eliminada)
                {
                    if (!string.IsNullOrEmpty(tarea.GoogleEventId)) await _remoto.EliminarAsync(tarea.GoogleEventId, ct);
                    await _storage.PurgarTareaAsync(tarea.Fecha, tarea.Id);
                }
                else
                {
                    var eventoId = await _remoto.GuardarAsync(tarea, ct);
                    await _storage.MarcarSincronizadaAsync(tarea.Fecha, tarea.Id, eventoId, tarea.ModificadaEn);
                }
                enviadas++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errores++;
                _logger.LogWarning(ex, "No se pudo enviar la tarea {Id} al calendario; se reintentará.", tarea.Id);
            }
        }

        return (enviadas, errores);
    }

    private sealed class EstadoSincronizacion
    {
        public DateTimeOffset? UltimaSincronizacion { get; set; }
    }

    private async Task<EstadoSincronizacion> LeerEstadoAsync()
    {
        try
        {
            if (!File.Exists(_rutaEstado)) return new EstadoSincronizacion();
            return JsonSerializer.Deserialize<EstadoSincronizacion>(await File.ReadAllTextAsync(_rutaEstado)) ?? new EstadoSincronizacion();
        }
        catch (JsonException)
        {
            // Si el archivo está dañado, se hace una sincronización completa: no se pierde nada.
            return new EstadoSincronizacion();
        }
    }

    private async Task GuardarEstadoAsync(EstadoSincronizacion estado)
    {
        var temporal = _rutaEstado + ".tmp";
        await File.WriteAllTextAsync(temporal, JsonSerializer.Serialize(estado));
        File.Move(temporal, _rutaEstado, overwrite: true);
    }

    /// <summary>Olvida la última sincronización: la próxima vez se traerá toda la ventana de fechas.</summary>
    public void Reiniciar()
    {
        if (File.Exists(_rutaEstado)) File.Delete(_rutaEstado);
    }

    public void Dispose()
    {
        _periodica?.Dispose();
        _esperaCambio?.Cancel();
    }
}
