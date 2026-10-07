using Microsoft.EntityFrameworkCore;
using Solar.Api.Agendamentos;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Conversas;

/// <summary>Grava falas, handoff e reserva do slot na mesma transacao.</summary>
public sealed class GravacaoDoTurno(SolarDbContext db, AgendaRepositorio agenda)
{
    public async Task<AgendamentoDaConversa?> SalvarAsync(
        Conversa conversa,
        Encaminhamento? encaminhamento,
        IReadOnlyList<SlotOferecido> oferecidos,
        long? slotEscolhido,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        if (encaminhamento is not null)
        {
            db.Encaminhamentos.Add(encaminhamento);
        }

        var evento = await agenda.ReservarAsync(
            conversa.Id,
            conversa.LeadId,
            oferecidos,
            slotEscolhido,
            agora,
            cancellationToken);

        if (evento is not null)
        {
            conversa.RegistrarAgendamento(evento.Estado, evento.Horario?.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        return evento;
    }

    public async Task<AgendamentoDaConversa> SalvarAgendamentoPorBotaoAsync(
        Conversa conversa,
        SlotOferecido horario,
        string corretor,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        var evento = await agenda.ReservarPorBotaoAsync(
            conversa.Id,
            conversa.LeadId,
            horario,
            agora,
            cancellationToken);

        if (evento.Estado != EstadosDoAgendamento.Confirmado)
        {
            return evento;
        }

        var textoLead = FormatarMensagemDoLead(horario.Inicio);
        var turno = new TurnoResponse(
            $"Combinado! {corretor} vai te chamar no contato que você deixou.",
            conversa.Lead.Intencao ?? Intencoes.Indefinida,
            new CamposExtraidos(),
            ProximasAcoes.AgendarReuniao,
            [],
            horario.Id,
            conversa.EssenciaisEm.HasValue);

        conversa.RegistrarTurno(textoLead, turno, agora);
        conversa.RegistrarAgendamento(evento.Estado, horario.Id);

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);

        return evento;
    }

    private static string FormatarMensagemDoLead(DateTimeOffset inicio)
    {
        var local = inicio.ToOffset(TimeSpan.FromHours(-3));
        var diaSemana = local.DayOfWeek switch
        {
            DayOfWeek.Sunday => "Domingo",
            DayOfWeek.Monday => "Segunda",
            DayOfWeek.Tuesday => "Terça",
            DayOfWeek.Wednesday => "Quarta",
            DayOfWeek.Thursday => "Quinta",
            DayOfWeek.Friday => "Sexta",
            DayOfWeek.Saturday => "Sábado",
            _ => "Segunda"
        };

        var mes = local.ToString("MMMM", new System.Globalization.CultureInfo("pt-BR"));
        var hora = local.Minute == 0 ? $"{local.Hour}h" : $"{local.Hour}h{local.Minute:D2}";

        return $"{diaSemana}, {local.Day} de {mes} às {hora}";
    }
}
