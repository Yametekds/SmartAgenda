using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace SmartAgenda.Services;

/// <summary>
/// Transcribe audio a texto usando Whisper (modelo de IA de código abierto) ejecutado
/// localmente en este mismo servidor: sin API key, sin cuenta externa y sin coste por uso.
/// Así el dictado por voz funciona desde cualquier navegador (solo necesita grabar audio,
/// algo que sí está soportado casi universalmente), en vez de depender del reconocimiento
/// de voz propio de cada navegador.
/// </summary>
public class TranscripcionAudioService
{
    private readonly string _modeloDir;
    private readonly string _modeloPath;
    private readonly OpcionesVoz _opciones;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly DeteccionVozService _deteccionVoz;
    private readonly ILogger<TranscripcionAudioService> _logger;
    private WhisperFactory? _factory;

    // Cuando no detecta voz real (música de fondo, aplausos, ruido...), Whisper no transcribe
    // nada "hablado": en vez de eso devuelve una anotación entre corchetes o paréntesis como
    // "[Music]", "[Música]" o "(ruido)". Un dictado de verdad nunca contiene esos símbolos, así
    // que se eliminan antes de tratar el resultado como una posible tarea. Esto queda como red de
    // seguridad adicional al filtro de voz (DeteccionVozService), que ya descarta la mayoría de
    // estos casos antes de siquiera llamar a Whisper.
    private static readonly Regex RegexEtiquetaNoHablada = new(@"\[[^\]]{0,60}\]|\([^)]{0,60}\)", RegexOptions.IgnoreCase);

    public TranscripcionAudioService(IRutasApp rutas, IOptions<OpcionesVoz> opciones, DeteccionVozService deteccionVoz, ILogger<TranscripcionAudioService> logger)
    {
        _modeloDir = Path.Combine(rutas.CarpetaDatos, "modelo-voz");
        Directory.CreateDirectory(_modeloDir);
        _opciones = opciones.Value;
        _modeloPath = Path.Combine(_modeloDir, ModeloVoz.NombreArchivo(_opciones.Modelo, _opciones.Cuantizacion));
        _deteccionVoz = deteccionVoz;
        _logger = logger;
    }

    /// <summary>
    /// Descarga (si hace falta) y carga el modelo en segundo plano al arrancar, para que el primer
    /// dictado no tenga que esperar a una descarga de cientos de MB.
    /// </summary>
    public async Task PrepararAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await AsegurarModeloAsync();
            _factory ??= WhisperFactory.FromPath(_modeloPath);
            _logger.LogInformation("Modelo de voz listo: {Modelo}.", Path.GetFileName(_modeloPath));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> TranscribirAsync(Stream audioWav, string idiomaWhisper = "es")
    {
        // Filtro previo de voz: TEMPORALMENTE en modo "solo diagnóstico" — se registra lo que
        // detecta pero NO bloquea la transcripción. Se está calibrando el umbral con datos reales
        // (ver logs); hasta tenerlo ajustado, es preferible dejar pasar algún falso "sin voz" de
        // Whisper antes que bloquear dictados que sí tenían voz real.
        try
        {
            audioWav.Position = 0;
            var muestras = LeerMuestrasWav(audioWav);

            double suma = 0;
            var pico = 0f;
            foreach (var m in muestras)
            {
                suma += (double)m * m;
                if (Math.Abs(m) > pico) pico = Math.Abs(m);
            }
            var rms = muestras.Length > 0 ? Math.Sqrt(suma / muestras.Length) : 0;
            _logger.LogInformation("Audio: RMS={Rms:F4}, pico={Pico:F4} (de 0 a 1)", rms, pico);

            await _deteccionVoz.TieneVozAsync(muestras);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo ejecutar el filtro de detección de voz; se continúa sin él.");
        }

        await _lock.WaitAsync();
        try
        {
            audioWav.Position = 0;
            await AsegurarModeloAsync();
            _factory ??= WhisperFactory.FromPath(_modeloPath);

            var idioma = idiomaWhisper is "en" ? "en" : "es";
            var builder = _factory.CreateBuilder()
                .WithLanguage(idioma)
                .WithPrompt(ModeloVoz.ContextoPara(idioma));
            if (_opciones.Hilos > 0) builder = builder.WithThreads(_opciones.Hilos);
            builder = _opciones.Decodificacion == EstrategiaDecodificacion.Greedy
                ? builder.WithGreedySamplingStrategy(_ => { })
                : builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(5));
            using var processor = builder.Build();

            var texto = new StringBuilder();
            await foreach (var segmento in processor.ProcessAsync(audioWav))
            {
                texto.Append(segmento.Text);
            }

            var sinEtiquetas = RegexEtiquetaNoHablada.Replace(texto.ToString(), " ");
            var resultado = Regex.Replace(sinEtiquetas, @"\s+", " ").Trim();
            if (ModeloVoz.EsEcoDelContexto(resultado, idioma))
            {
                _logger.LogInformation("Whisper devolvió el propio contexto (audio sin voz); se descarta.");
                resultado = string.Empty;
            }
            _logger.LogInformation("Whisper: texto crudo \"{Crudo}\" -> texto final \"{Final}\"", texto.ToString(), resultado);
            return resultado;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Lee las muestras PCM de un WAV de 16 bits como floats en [-1, 1]. Asume el
    /// formato simple (RIFF/WAVE/fmt /data) que genera nuestro propio codificador en el navegador.</summary>
    private static float[] LeerMuestrasWav(Stream wav)
    {
        using var reader = new BinaryReader(wav, Encoding.ASCII, leaveOpen: true);
        reader.ReadBytes(4); // "RIFF"
        reader.ReadInt32(); // tamaño del archivo
        reader.ReadBytes(4); // "WAVE"

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "data")
            {
                var bytes = reader.ReadBytes(chunkSize);
                var muestras = new float[bytes.Length / 2];
                for (var i = 0; i < muestras.Length; i++)
                {
                    muestras[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                }
                return muestras;
            }

            reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
        }

        return [];
    }

    private async Task AsegurarModeloAsync()
    {
        if (File.Exists(_modeloPath)) return;

        // Primera vez: descarga el modelo una sola vez a Data/modelo-voz/, comprobando su huella
        // SHA-256 antes de darlo por bueno. Si la descarga se corta o llega alterada, no se usa.
        _logger.LogInformation("Descargando el modelo de voz {Modelo} (~{Mb} MB). Solo ocurre la primera vez.",
            Path.GetFileName(_modeloPath), ModeloVoz.TamanoMb(_opciones.Modelo, _opciones.Cuantizacion)?.ToString() ?? "?");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var descargador = new WhisperGgmlDownloader(httpClient);
        var temporal = _modeloPath + ".tmp";
        try
        {
            await using (var modeloStream = await descargador.GetGgmlModelAsync(_opciones.Modelo, _opciones.Cuantizacion))
            await using (var archivo = File.Create(temporal))
            {
                await ModeloVoz.CopiarVerificandoAsync(modeloStream, archivo, ModeloVoz.Sha256Esperado(_opciones.Modelo, _opciones.Cuantizacion));
            }
            File.Move(temporal, _modeloPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporal)) File.Delete(temporal);
            throw;
        }

        BorrarModelosAnteriores();
    }

    /// <summary>Libera espacio: borra los modelos de voz de versiones anteriores que ya no se usan.</summary>
    private void BorrarModelosAnteriores()
    {
        foreach (var archivo in Directory.EnumerateFiles(_modeloDir, "ggml-*.bin"))
        {
            if (string.Equals(archivo, _modeloPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(archivo);
                _logger.LogInformation("Modelo de voz anterior borrado: {Archivo}.", Path.GetFileName(archivo));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo borrar el modelo de voz anterior {Archivo}.", Path.GetFileName(archivo));
            }
        }
    }
}
