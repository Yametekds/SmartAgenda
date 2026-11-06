using System.Security.Cryptography;
using Whisper.net.Ggml;

namespace SmartAgenda.Services;

/// <summary>
/// Configuración del reconocimiento de voz (sección "Voz" de appsettings.json).
/// Por defecto usa Whisper large-v3-turbo cuantizado en Q5_0: mucho más preciso que "small"
/// ocupando apenas un poco más en disco.
/// </summary>
public class OpcionesVoz
{
    public const string Seccion = "Voz";

    /// <summary>Modelo de Whisper: "LargeV3Turbo" (por defecto) o "Small" (modo ligero).</summary>
    public GgmlType Modelo { get; set; } = GgmlType.LargeV3Turbo;

    /// <summary>"Q5_0" (por defecto), "Q8_0" (algo más preciso y más pesado) o "NoQuantization".</summary>
    public QuantizationType Cuantizacion { get; set; } = QuantizationType.Q5_0;

    /// <summary>
    /// "BeamSearch" (por defecto): prueba varias hipótesis a la vez y se queda con la mejor; más preciso.
    /// "Greedy": más rápido, pensado para equipos con poca potencia.
    /// </summary>
    public EstrategiaDecodificacion Decodificacion { get; set; } = EstrategiaDecodificacion.BeamSearch;

    /// <summary>Hilos de CPU para Whisper. 0 = los que decida la librería.</summary>
    public int Hilos { get; set; }
}

public enum EstrategiaDecodificacion
{
    BeamSearch,
    Greedy
}

/// <summary>
/// Catálogo de modelos de voz: nombre de archivo, tamaño y huella SHA-256 publicada de cada uno,
/// para comprobar que la descarga llegó completa y sin alterar antes de usarla.
/// </summary>
public static class ModeloVoz
{
    private static readonly Dictionary<(GgmlType, QuantizationType), (long Bytes, string Sha256)> Conocidos = new()
    {
        [(GgmlType.LargeV3Turbo, QuantizationType.Q5_0)] = (574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        [(GgmlType.LargeV3Turbo, QuantizationType.Q8_0)] = (874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"),
        [(GgmlType.Small, QuantizationType.Q5_0)] = (175_209_663, "23ad5215d37677f8eb8cbc41b4d0d4e451229f811be5206ff116dd9337eba748"),
        [(GgmlType.Small, QuantizationType.NoQuantization)] = (487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
    };

    /// <summary>Nombre del archivo en Data/modelo-voz/, p. ej. "ggml-largev3turbo-q5_0.bin".</summary>
    public static string NombreArchivo(GgmlType modelo, QuantizationType cuantizacion)
    {
        var sufijo = cuantizacion == QuantizationType.NoQuantization ? "" : "-" + cuantizacion.ToString().ToLowerInvariant();
        return $"ggml-{modelo.ToString().ToLowerInvariant()}{sufijo}.bin";
    }

    /// <summary>Huella SHA-256 esperada, o null si el modelo no está en el catálogo (entonces no se comprueba).</summary>
    public static string? Sha256Esperado(GgmlType modelo, QuantizationType cuantizacion) =>
        Conocidos.TryGetValue((modelo, cuantizacion), out var info) ? info.Sha256 : null;

    /// <summary>Tamaño aproximado en MB, para avisos en el log.</summary>
    public static long? TamanoMb(GgmlType modelo, QuantizationType cuantizacion) =>
        Conocidos.TryGetValue((modelo, cuantizacion), out var info) ? info.Bytes / 1_000_000 : null;

    /// <summary>
    /// Copia <paramref name="origen"/> en <paramref name="destino"/> calculando a la vez su SHA-256,
    /// y lanza <see cref="InvalidDataException"/> si no coincide con <paramref name="sha256Esperado"/>.
    /// </summary>
    public static async Task CopiarVerificandoAsync(Stream origen, Stream destino, string? sha256Esperado, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int leidos;
        while ((leidos = await origen.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, leidos);
            await destino.WriteAsync(buffer.AsMemory(0, leidos), ct);
        }

        if (sha256Esperado is null) return;

        var obtenido = Convert.ToHexString(hash.GetHashAndReset());
        if (!obtenido.Equals(sha256Esperado, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"El modelo de voz descargado no coincide con su huella SHA-256 (esperada {sha256Esperado}, obtenida {obtenido.ToLowerInvariant()}).");
        }
    }

    /// <summary>
    /// Vocabulario de agenda que se le da a Whisper como contexto. Le orienta hacia fechas, horas y
    /// palabras habituales en una tarea, que es donde más fallaba.
    /// </summary>
    public static string ContextoPara(string idioma) => idioma == "en"
        ? "Calendar entry. Monday, Tuesday, Wednesday, Thursday, Friday, Saturday, Sunday, today, tomorrow, next week. At 9 a.m., at half past five, at quarter to ten, at noon. Meeting, appointment, dentist, call, gym, for an hour."
        : "Entrada de agenda. Lunes, martes, miércoles, jueves, viernes, sábado, domingo, hoy, mañana, pasado mañana, la semana que viene. A las 9, a las cinco y media, a las diez menos cuarto, a mediodía. Reunión, cita, dentista, llamar, gimnasio, durante una hora.";

    /// <summary>
    /// Con audio sin voz, Whisper a veces "repite" el contexto que se le dio. Si lo transcrito es
    /// un trozo largo de ese contexto, no es un dictado real.
    /// </summary>
    public static bool EsEcoDelContexto(string texto, string idioma)
    {
        static string Normalizar(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var t = Normalizar(texto);
        return t.Length >= 30 && Normalizar(ContextoPara(idioma)).Contains(t);
    }
}
