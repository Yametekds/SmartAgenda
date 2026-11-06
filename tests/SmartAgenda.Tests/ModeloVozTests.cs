using System.Security.Cryptography;
using System.Text;
using SmartAgenda.Services;
using Whisper.net.Ggml;

namespace SmartAgenda.Tests;

public class ModeloVozTests
{
    [Fact]
    public void Por_defecto_usa_turbo_cuantizado_con_beam_search()
    {
        var opciones = new OpcionesVoz();
        Assert.Equal(GgmlType.LargeV3Turbo, opciones.Modelo);
        Assert.Equal(QuantizationType.Q5_0, opciones.Cuantizacion);
        Assert.Equal(EstrategiaDecodificacion.BeamSearch, opciones.Decodificacion);
    }

    [Theory]
    [InlineData(GgmlType.LargeV3Turbo, QuantizationType.Q5_0, "ggml-largev3turbo-q5_0.bin")]
    [InlineData(GgmlType.LargeV3Turbo, QuantizationType.Q8_0, "ggml-largev3turbo-q8_0.bin")]
    [InlineData(GgmlType.Small, QuantizationType.NoQuantization, "ggml-small.bin")]
    public void Nombre_de_archivo_distinto_por_modelo_y_cuantizacion(GgmlType modelo, QuantizationType cuantizacion, string esperado)
    {
        Assert.Equal(esperado, ModeloVoz.NombreArchivo(modelo, cuantizacion));
    }

    [Fact]
    public void Los_modelos_del_catalogo_tienen_huella()
    {
        Assert.Matches("^[0-9a-f]{64}$", ModeloVoz.Sha256Esperado(GgmlType.LargeV3Turbo, QuantizationType.Q5_0));
        Assert.Matches("^[0-9a-f]{64}$", ModeloVoz.Sha256Esperado(GgmlType.Small, QuantizationType.NoQuantization));
        Assert.Null(ModeloVoz.Sha256Esperado(GgmlType.Tiny, QuantizationType.Q4_0));
    }

    [Fact]
    public async Task Copia_y_acepta_si_la_huella_coincide()
    {
        var datos = Encoding.UTF8.GetBytes("modelo de prueba");
        var huella = Convert.ToHexString(SHA256.HashData(datos)).ToLowerInvariant();
        using var destino = new MemoryStream();

        await ModeloVoz.CopiarVerificandoAsync(new MemoryStream(datos), destino, huella);

        Assert.Equal(datos, destino.ToArray());
    }

    [Fact]
    public async Task Rechaza_si_la_huella_no_coincide()
    {
        var datos = Encoding.UTF8.GetBytes("modelo alterado");
        using var destino = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModeloVoz.CopiarVerificandoAsync(new MemoryStream(datos), destino, new string('0', 64)));
    }

    [Fact]
    public async Task Sin_huella_conocida_copia_sin_comprobar()
    {
        var datos = new byte[200_000];
        Random.Shared.NextBytes(datos);
        using var destino = new MemoryStream();

        await ModeloVoz.CopiarVerificandoAsync(new MemoryStream(datos), destino, null);

        Assert.Equal(datos, destino.ToArray());
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public void Detecta_cuando_Whisper_repite_el_contexto(string idioma)
    {
        Assert.True(ModeloVoz.EsEcoDelContexto(ModeloVoz.ContextoPara(idioma), idioma));
    }

    [Theory]
    [InlineData("Reunión con Ana mañana a las cinco y media", "es")]
    [InlineData("Dentista el lunes a las diez", "es")]
    [InlineData("Meeting with Ana tomorrow at half past five", "en")]
    [InlineData("Lunes", "es")]
    public void Un_dictado_normal_no_se_descarta(string texto, string idioma)
    {
        Assert.False(ModeloVoz.EsEcoDelContexto(texto, idioma));
    }
}
