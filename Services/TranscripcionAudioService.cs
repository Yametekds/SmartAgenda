using System.Text;
using System.Text.RegularExpressions;
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
    private readonly string _modeloPath;
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

    public TranscripcionAudioService(IWebHostEnvironment env, DeteccionVozService deteccionVoz, ILogger<TranscripcionAudioService> logger)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data", "modelo-voz");
        Directory.CreateDirectory(dir);
        // "small" en vez de "base": más preciso con ruido/música de fondo, a cambio de ser más
        // lento y pesar más (~466 MB en vez de ~142 MB). Cambia a GgmlType.Base más abajo si
        // prefieres priorizar velocidad sobre precisión.
        _modeloPath = Path.Combine(dir, "ggml-small.bin");
        _deteccionVoz = deteccionVoz;
        _logger = logger;
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

            using var processor = _factory.CreateBuilder()
                .WithLanguage(idiomaWhisper is "en" ? "en" : "es")
                .Build();

            var texto = new StringBuilder();
            await foreach (var segmento in processor.ProcessAsync(audioWav))
            {
                texto.Append(segmento.Text);
            }

            var sinEtiquetas = RegexEtiquetaNoHablada.Replace(texto.ToString(), " ");
            var resultado = Regex.Replace(sinEtiquetas, @"\s+", " ").Trim();
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

        // Primera vez: descarga el modelo (~466 MB con "small") una sola vez y lo deja en Data/modelo-voz/.
        using var httpClient = new HttpClient();
        var descargador = new WhisperGgmlDownloader(httpClient);
        await using var modeloStream = await descargador.GetGgmlModelAsync(GgmlType.Small);
        var temporal = _modeloPath + ".tmp";
        await using (var archivo = File.Create(temporal))
        {
            await modeloStream.CopyToAsync(archivo);
        }
        File.Move(temporal, _modeloPath, overwrite: true);
    }
}
