using SmartAgenda.Models;
using SmartAgenda.Services;

namespace SmartAgenda.Tests;

public class ParserInglesTests
{
    // Wednesday, October 28, 2026: every relative date is computed from here.
    private static readonly DateOnly Hoy = new(2026, 10, 28);
    private readonly ParserLenguajeNaturalService _parser = new();

    private List<EventoPropuesto> Parsear(string texto) => _parser.ParsearMultiple(texto, Idioma.En, Hoy);

    private EventoPropuesto ParsearUno(string texto)
    {
        var eventos = Parsear(texto);
        Assert.Single(eventos);
        return eventos[0];
    }

    [Fact]
    public void Tomorrow_with_pm()
    {
        var ev = ParsearUno("dentist tomorrow at 10pm");
        Assert.Equal("Dentist", ev.Texto);
        Assert.Equal(new DateOnly(2026, 10, 29), ev.Fecha);
        Assert.Equal(new TimeOnly(22, 0), ev.Hora);
    }

    [Fact]
    public void Half_past()
    {
        var ev = ParsearUno("dinner tomorrow at half past seven pm");
        Assert.Equal("Dinner", ev.Texto);
        Assert.Equal(new TimeOnly(19, 30), ev.Hora);
    }

    [Fact]
    public void Quarter_to()
    {
        var ev = ParsearUno("train tomorrow at quarter to 8 am");
        Assert.Equal(new TimeOnly(7, 45), ev.Hora);
    }

    [Fact]
    public void Minutes_past()
    {
        var ev = ParsearUno("call Ana tomorrow at ten past 11");
        Assert.Equal(new TimeOnly(11, 10), ev.Hora);
    }

    [Fact]
    public void Number_to_number_is_not_a_time()
    {
        var ev = ParsearUno("party for 5 to 10 people tomorrow at 9");
        Assert.Equal(new TimeOnly(9, 0), ev.Hora);
    }

    [Fact]
    public void Time_without_am_pm_between_1_and_7_assumes_afternoon()
    {
        var ev = ParsearUno("lunch tomorrow at 1");
        Assert.Equal(new TimeOnly(13, 0), ev.Hora);
        Assert.NotNull(ev.AvisoHora);
        Assert.Contains("13:00", ev.AvisoHora);
    }

    [Fact]
    public void Past_month_rolls_to_next_year()
    {
        var ev = ParsearUno("meeting on May 5th at 3pm");
        Assert.Equal(new DateOnly(2027, 5, 5), ev.Fecha);
        Assert.Equal(new TimeOnly(15, 0), ev.Hora);
    }

    [Fact]
    public void May_as_a_verb_is_not_a_month()
    {
        var eventos = Parsear("tomorrow at 10am I may call Ana and her brother");
        var ev = Assert.Single(eventos);
        Assert.Equal(new DateOnly(2026, 10, 29), ev.Fecha);
        Assert.Equal(new TimeOnly(10, 0), ev.Hora);
    }

    [Fact]
    public void Several_events()
    {
        var eventos = Parsear("dentist tomorrow at 10am and gym on Friday at 6pm");
        Assert.Equal(2, eventos.Count);
        Assert.Equal(new DateOnly(2026, 10, 30), eventos[1].Fecha);
        Assert.Equal(new TimeOnly(18, 0), eventos[1].Hora);
    }

    [Fact]
    public void Duration()
    {
        var ev = ParsearUno("tennis tomorrow at 6pm for 90 minutes");
        Assert.Equal(TimeSpan.FromMinutes(90), ev.Duracion);
    }
}
