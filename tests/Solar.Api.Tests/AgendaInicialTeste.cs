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

    [Fact]
    public void Gera_sequencia_exata_9_14_19_horario_sao_paulo_com_saida_utc_e_duracao_de_uma_hora()
    {
        var agora = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.FromHours(-3));

        var horarios = AgendaInicial.ProximosHorarios(agora).Take(6).ToArray();

        Assert.Equal(6, horarios.Length);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), horarios[0].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero), horarios[0].Fim);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero), horarios[1].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 18, 0, 0, TimeSpan.Zero), horarios[1].Fim);

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero), horarios[2].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero), horarios[2].Fim);

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), horarios[3].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero), horarios[3].Fim);

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 17, 0, 0, TimeSpan.Zero), horarios[4].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 18, 0, 0, TimeSpan.Zero), horarios[4].Fim);

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 22, 0, 0, TimeSpan.Zero), horarios[5].Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 23, 0, 0, TimeSpan.Zero), horarios[5].Fim);

        var sp = TimeSpan.FromHours(-3);
        Assert.Equal(new TimeSpan(9, 0, 0), horarios[0].Inicio.ToOffset(sp).TimeOfDay);
        Assert.Equal(new TimeSpan(14, 0, 0), horarios[1].Inicio.ToOffset(sp).TimeOfDay);
        Assert.Equal(new TimeSpan(19, 0, 0), horarios[2].Inicio.ToOffset(sp).TimeOfDay);
        Assert.Equal(new TimeSpan(9, 0, 0), horarios[3].Inicio.ToOffset(sp).TimeOfDay);
        Assert.Equal(new TimeSpan(14, 0, 0), horarios[4].Inicio.ToOffset(sp).TimeOfDay);
        Assert.Equal(new TimeSpan(19, 0, 0), horarios[5].Inicio.ToOffset(sp).TimeOfDay);

        Assert.All(horarios, h => Assert.Equal(TimeSpan.Zero, h.Inicio.Offset));
        Assert.All(horarios, h => Assert.Equal(TimeSpan.Zero, h.Fim.Offset));
        Assert.All(horarios, h => Assert.Equal(TimeSpan.FromHours(1), h.Fim - h.Inicio));
    }

    [Fact]
    public void Sexta_apos_19h_avanca_para_segunda_as_9h()
    {
        var sextaApos19 = new DateTimeOffset(2026, 10, 9, 19, 0, 1, TimeSpan.FromHours(-3));

        var proximo = AgendaInicial.ProximosHorarios(sextaApos19).First();

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), proximo.Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero), proximo.Fim);
        Assert.Equal(new TimeSpan(9, 0, 0), proximo.Inicio.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay);
        Assert.Equal(DayOfWeek.Monday, proximo.Inicio.ToOffset(TimeSpan.FromHours(-3)).DayOfWeek);
    }

    [Fact]
    public void Exclui_slot_cujo_inicio_coincide_exatamente_com_agora()
    {
        var exatamenteAsNove = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(-3));

        var proximo = AgendaInicial.ProximosHorarios(exatamenteAsNove).First();

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero), proximo.Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 18, 0, 0, TimeSpan.Zero), proximo.Fim);
        Assert.Equal(new TimeSpan(14, 0, 0), proximo.Inicio.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay);
    }

    [Fact]
    public void Trata_virada_de_dia_entre_utc_e_sao_paulo()
    {
        var inicioMadrugadaUtc = new DateTimeOffset(2026, 10, 6, 1, 30, 0, TimeSpan.Zero);

        var primeiro = AgendaInicial.ProximosHorarios(inicioMadrugadaUtc).First();

        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), primeiro.Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero), primeiro.Fim);
        Assert.Equal(new TimeSpan(9, 0, 0), primeiro.Inicio.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay);

        var finalTardeUtc = new DateTimeOffset(2026, 10, 5, 21, 30, 0, TimeSpan.Zero);

        var segundo = AgendaInicial.ProximosHorarios(finalTardeUtc).First();

        Assert.Equal(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero), segundo.Inicio);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero), segundo.Fim);
        Assert.Equal(new TimeSpan(19, 0, 0), segundo.Inicio.ToOffset(TimeSpan.FromHours(-3)).TimeOfDay);
    }
}
