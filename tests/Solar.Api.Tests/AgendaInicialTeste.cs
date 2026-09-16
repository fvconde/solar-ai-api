using Solar.Api.Agendamentos;

namespace Solar.Api.Tests;

public class AgendaInicialTeste
{
    [Fact]
    public void Gera_apenas_horarios_futuros_em_dia_util()
    {
        var agora = new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.FromHours(-3));

        var horarios = AgendaInicial.ProximosHorarios(agora).Take(6).ToArray();

        Assert.All(horarios, horario => Assert.True(horario.Inicio > agora));
        Assert.All(horarios, horario => Assert.Equal(TimeSpan.Zero, horario.Inicio.Offset));
        Assert.All(horarios, horario => Assert.Equal(TimeSpan.FromHours(1), horario.Fim - horario.Inicio));
        Assert.All(horarios, horario => Assert.DoesNotContain(
            horario.Inicio.DayOfWeek,
            new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }));
    }

    [Fact]
    public void Boot_em_outra_data_produz_agenda_relativa_a_essa_data()
    {
        var primeiroBoot = new DateTimeOffset(2026, 9, 10, 20, 0, 0, TimeSpan.Zero);
        var segundoBoot = primeiroBoot.AddDays(7);

        var primeiro = AgendaInicial.ProximosHorarios(primeiroBoot).First();
        var segundo = AgendaInicial.ProximosHorarios(segundoBoot).First();

        Assert.True(segundo.Inicio > primeiro.Inicio);
        Assert.True(segundo.Inicio > segundoBoot);
    }
}
