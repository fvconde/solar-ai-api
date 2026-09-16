using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Persistencia;

namespace Solar.Api.Agendamentos;

public sealed class AgendaRepositorio(SolarDbContext db)
{
    private const int LimiteDeOferta = 3;

    public async Task<IReadOnlyList<SlotOferecido>> OfertarAsync(
        Guid conversaId,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        var corretorId = await db.Encaminhamentos
            .AsNoTracking()
            .Where(encaminhamento => encaminhamento.ConversaId == conversaId)
            .Select(encaminhamento => encaminhamento.CorretorId)
            .FirstOrDefaultAsync(cancellationToken);

        if (corretorId is null)
        {
            return [];
        }

        return await OfertarDoCorretorAsync(corretorId.Value, agora, cancellationToken);
    }

    internal async Task<AgendamentoDaConversa?> ReservarAsync(
        Guid conversaId,
        Guid leadId,
        IReadOnlyList<SlotOferecido> oferecidos,
        long? escolhido,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (escolhido is null)
        {
            return null;
        }

        var horario = oferecidos.FirstOrDefault(slot => slot.Id == escolhido.Value);

        // Segunda barreira alem do gate do agente: a API dona do banco nunca
        // aceita um id que nao tenha saído da consulta deste turno.
        if (horario is null)
        {
            return null;
        }

        var atualizados = await db.Slots
            .Where(slot =>
                slot.Id == escolhido.Value &&
                slot.LeadId == null &&
                slot.Inicio > agora &&
                db.Encaminhamentos.Any(encaminhamento =>
                    encaminhamento.ConversaId == conversaId &&
                    encaminhamento.CorretorId == slot.CorretorId))
            .ExecuteUpdateAsync(
                atualizacao => atualizacao.SetProperty(slot => slot.LeadId, leadId),
                cancellationToken);

        if (atualizados == 1)
        {
            return new AgendamentoDaConversa(
                EstadosDoAgendamento.Confirmado,
                horario,
                []);
        }

        var alternativas = await OfertarAsync(conversaId, agora, cancellationToken);

        return new AgendamentoDaConversa(
            EstadosDoAgendamento.Indisponivel,
            null,
            alternativas);
    }

    private async Task<IReadOnlyList<SlotOferecido>> OfertarDoCorretorAsync(
        Guid corretorId,
        DateTimeOffset agora,
        CancellationToken cancellationToken) =>
        await db.Slots
            .AsNoTracking()
            .Where(slot =>
                slot.CorretorId == corretorId &&
                slot.LeadId == null &&
                slot.Inicio > agora)
            .OrderBy(slot => slot.Inicio)
            .Take(LimiteDeOferta)
            .Select(slot => new SlotOferecido(slot.Id, slot.Inicio, slot.Fim))
            .ToListAsync(cancellationToken);
}
