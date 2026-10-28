using SmartAgenda.Models;
using SmartAgenda.Services;

namespace SmartAgenda.Tests;

public class ParserEspanolTests
{
    // Miércoles 28 de octubre de 2026: todas las fechas relativas se calculan a partir de aquí.
    private static readonly DateOnly Hoy = new(2026, 10, 28);
    private readonly ParserLenguajeNaturalService _parser = new();

    private List<EventoPropuesto> Parsear(string texto) => _parser.ParsearMultiple(texto, Idioma.Es, Hoy);

    private EventoPropuesto ParsearUno(string texto)
    {
        var eventos = Parsear(texto);
        Assert.Single(eventos);
        return eventos[0];
    }

    [Fact]
    public void Manana_con_hora_de_mañana()
    {
        var ev = ParsearUno("dentista mañana a las 10");
        Assert.Equal("Dentista", ev.Texto);
        Assert.Equal(new DateOnly(2026, 10, 29), ev.Fecha);
        Assert.Equal(new TimeOnly(10, 0), ev.Hora);
        Assert.Null(ev.AvisoHora);
        Assert.False(ev.EsAmbiguo);
    }

    [Fact]
    public void Dia_de_la_semana_con_periodo_de_noche()
    {
        var ev = ParsearUno("cena el viernes a las 9 de la noche");
        Assert.Equal(new DateOnly(2026, 10, 30), ev.Fecha);
        Assert.Equal(new TimeOnly(21, 0), ev.Hora);
    }

    [Fact]
    public void Y_media_no_parte_el_evento_ni_queda_en_el_titulo()
    {
        var ev = ParsearUno("reunión mañana a las 10 y media");
        Assert.Equal("Reunión", ev.Texto);
        Assert.Equal(new TimeOnly(10, 30), ev.Hora);
    }

    [Fact]
    public void Y_cuarto_con_periodo_de_la_mañana()
    {
        var ev = ParsearUno("café mañana a las siete y cuarto de la mañana");
        Assert.Equal("Café", ev.Texto);
        Assert.Equal(new DateOnly(2026, 10, 29), ev.Fecha);
        Assert.Equal(new TimeOnly(7, 15), ev.Hora);
        Assert.Null(ev.AvisoHora);
    }

    [Fact]
    public void Y_minutos_en_numero()
    {
        var ev = ParsearUno("llamar a Ana mañana a las 11 y 20");
        Assert.Equal(new TimeOnly(11, 20), ev.Hora);
    }

    [Fact]
    public void Menos_cuarto_resta_de_la_hora_dicha()
    {
        var ev = ParsearUno("tren mañana a las 9 menos cuarto");
        Assert.Equal(new TimeOnly(8, 45), ev.Hora);
    }

    [Fact]
    public void Hora_sin_periodo_entre_1_y_7_se_asume_por_la_tarde_y_avisa()
    {
        var ev = ParsearUno("gimnasio mañana a las 7");
        Assert.Equal(new TimeOnly(19, 0), ev.Hora);
        Assert.NotNull(ev.AvisoHora);
        Assert.Contains("19:00", ev.AvisoHora);
    }

    [Fact]
    public void Hora_con_cero_delante_se_respeta_como_24h()
    {
        var ev = ParsearUno("gimnasio mañana a las 07:00");
        Assert.Equal(new TimeOnly(7, 0), ev.Hora);
        Assert.Null(ev.AvisoHora);
    }

    [Fact]
    public void Hora_en_formato_24h_no_avisa()
    {
        var ev = ParsearUno("gimnasio mañana a las 18:00");
        Assert.Equal(new TimeOnly(18, 0), ev.Hora);
        Assert.Null(ev.AvisoHora);
    }

    [Fact]
    public void Fecha_ya_pasada_este_año_pasa_al_siguiente()
    {
        var ev = ParsearUno("revisión del coche el 5 de enero");
        Assert.Equal(new DateOnly(2027, 1, 5), ev.Fecha);
    }

    [Fact]
    public void Fecha_futura_de_este_año_se_queda_en_este_año()
    {
        var ev = ParsearUno("cumpleaños de Lucía el 20 de diciembre");
        Assert.Equal(new DateOnly(2026, 12, 20), ev.Fecha);
    }

    [Fact]
    public void Numero_suelto_ya_pasado_va_al_mes_siguiente_y_se_marca_ambiguo()
    {
        var ev = ParsearUno("pagar el alquiler el 3");
        Assert.Equal(new DateOnly(2026, 11, 3), ev.Fecha);
        Assert.True(ev.EsAmbiguo);
    }

    [Fact]
    public void Varios_eventos_en_una_frase()
    {
        var eventos = Parsear("dentista mañana a las 10 y gimnasio el viernes a las 18:00");
        Assert.Equal(2, eventos.Count);
        Assert.Equal("Dentista", eventos[0].Texto);
        Assert.Equal(new DateOnly(2026, 10, 29), eventos[0].Fecha);
        Assert.Equal("Gimnasio", eventos[1].Texto);
        Assert.Equal(new DateOnly(2026, 10, 30), eventos[1].Fecha);
        Assert.Equal(new TimeOnly(18, 0), eventos[1].Hora);
    }

    [Fact]
    public void Y_dentro_del_titulo_no_crea_otro_evento()
    {
        var ev = ParsearUno("comida con mis padres y mi hermano mañana a las 14:00");
        Assert.Equal("Comida con mis padres y mi hermano", ev.Texto);
    }

    [Fact]
    public void Hora_con_y_media_seguida_de_otro_evento()
    {
        var eventos = Parsear("reunión mañana a las 9 y media de la mañana, y cena el sábado a las 21:00");
        Assert.Equal(2, eventos.Count);
        Assert.Equal(new TimeOnly(9, 30), eventos[0].Hora);
        Assert.Equal(new TimeOnly(21, 0), eventos[1].Hora);
        Assert.Equal(new DateOnly(2026, 10, 31), eventos[1].Fecha);
    }

    [Fact]
    public void Duracion()
    {
        var ev = ParsearUno("partido de pádel mañana a las 19:00 durante 2 horas");
        Assert.Equal(TimeSpan.FromHours(2), ev.Duracion);
        Assert.Equal("Partido de pádel", ev.Texto);
    }

    [Fact]
    public void Pasado_mañana_y_dentro_de_dias()
    {
        Assert.Equal(new DateOnly(2026, 10, 30), ParsearUno("médico pasado mañana").Fecha);
        Assert.Equal(new DateOnly(2026, 11, 2), ParsearUno("entregar informe dentro de 5 días").Fecha);
    }

    [Fact]
    public void Tras_un_punto_lo_anterior_sigue_siendo_otro_evento()
    {
        var eventos = Parsear("comprar pan. dentista mañana a las 10");
        Assert.Equal(2, eventos.Count);
        Assert.Equal("Comprar pan", eventos[0].Texto);
        Assert.Equal("Dentista", eventos[1].Texto);
    }

    [Fact]
    public void Dia_de_la_semana_con_numero_que_era_una_hora()
    {
        var ev = ParsearUno("cine el domingo las 6");
        Assert.Equal(new DateOnly(2026, 11, 1), ev.Fecha);
        Assert.Equal(new TimeOnly(18, 0), ev.Hora);
    }
}
