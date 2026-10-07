using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SmartAgenda.Services;

/// <summary>
/// Detecta si un audio contiene voz humana real, usando Silero VAD
/// (https://github.com/snakers4/silero-vad) ejecutado localmente vía ONNX Runtime — sin API key,
/// sin cuenta externa, sin coste. Se usa como filtro antes de Whisper: si no hay voz (música de
/// fondo, ruido, silencio...), no tiene sentido transcribir y arriesgarse a que Whisper
/// "alucine" texto a partir de audio no hablado.
/// </summary>
public class DeteccionVozService
{
    private const int SampleRate = 16000;
    private const int TamanoVentana = 512; // muestras por ventana a 16kHz, el tamaño con el que se entrenó Silero VAD
    // 0.5 es el umbral "de manual" de Silero VAD, pensado para audio limpio bien grabado. Con
    // micrófono de móvil + nuestra canalización más simple, la voz real medida en pruebas reales
    // se quedaba muy por debajo (máximos de 0.02-0.24 en grabaciones que sí tenían voz) — así que
    // se baja a un valor acorde a esos datos, no al ideal teórico.
    private const float UmbralVoz = 0.2f;

    private readonly string _modeloPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<DeteccionVozService> _logger;
    private InferenceSession? _sesion;

    public DeteccionVozService(IWebHostEnvironment env, ILogger<DeteccionVozService> logger)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data", "modelo-voz");
        Directory.CreateDirectory(dir);
        _modeloPath = Path.Combine(dir, "silero-vad.onnx");
        _logger = logger;
    }

    /// <summary>
    /// Verdadero si en algún tramo de las muestras (PCM float, 16kHz, mono) se detecta voz por
    /// encima del umbral. Si el audio es demasiado corto para analizar, asume que sí hay voz
    /// (que lo decida Whisper) en vez de descartarlo a ciegas.
    /// </summary>
    public async Task<bool> TieneVozAsync(float[] muestras)
    {
        _logger.LogInformation("VAD: recibidas {N} muestras ({Seg:F1}s)", muestras.Length, muestras.Length / (double)SampleRate);

        if (muestras.Length < TamanoVentana)
        {
            _logger.LogInformation("VAD: audio demasiado corto para analizar, se asume que hay voz.");
            return true;
        }

        await _lock.WaitAsync();
        try
        {
            await AsegurarModeloAsync();
            _sesion ??= new InferenceSession(_modeloPath);

            var estado = new DenseTensor<float>(new[] { 2, 1, 128 });
            var srTensor = new DenseTensor<long>(new long[] { SampleRate }, Array.Empty<int>());
            var probabilidadMaxima = 0f;

            for (var i = 0; i + TamanoVentana <= muestras.Length; i += TamanoVentana)
            {
                var ventana = new DenseTensor<float>(new[] { 1, TamanoVentana });
                for (var j = 0; j < TamanoVentana; j++) ventana[0, j] = muestras[i + j];

                var entradas = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input", ventana),
                    NamedOnnxValue.CreateFromTensor("state", estado),
                    NamedOnnxValue.CreateFromTensor("sr", srTensor)
                };

                using var resultados = _sesion.Run(entradas);
                var probabilidad = resultados.First(r => r.Name == "output").AsEnumerable<float>().First();
                estado = resultados.First(r => r.Name == "stateN").AsTensor<float>().ToDenseTensor();

                if (probabilidad > probabilidadMaxima) probabilidadMaxima = probabilidad;

                if (probabilidad >= UmbralVoz)
                {
                    _logger.LogInformation("VAD: voz detectada (probabilidad {P:F3} en ventana @ {Ms}ms).", probabilidad, i * 1000 / SampleRate);
                    return true;
                }
            }

            _logger.LogInformation("VAD: no se superó el umbral ({Umbral}). Probabilidad máxima observada: {Max:F3}.", UmbralVoz, probabilidadMaxima);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task AsegurarModeloAsync()
    {
        if (File.Exists(_modeloPath)) return;

        // Primera vez: descarga el modelo (~2 MB) una sola vez y lo deja en Data/modelo-voz/.
        using var httpClient = new HttpClient();
        var bytes = await httpClient.GetByteArrayAsync(
            "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx");
        var temporal = _modeloPath + ".tmp";
        await File.WriteAllBytesAsync(temporal, bytes);
        File.Move(temporal, _modeloPath, overwrite: true);
    }
}
