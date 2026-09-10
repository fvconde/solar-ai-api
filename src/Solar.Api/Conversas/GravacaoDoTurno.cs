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
}
