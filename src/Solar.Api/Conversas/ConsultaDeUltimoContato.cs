using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Conversas;

internal static class ConsultaDeUltimoContato
{
    public static async Task<List<(Guid LeadId, DateTimeOffset Em)>> ObterAsync(
        SolarDbContext db,
        IQueryable<Lead> consultaLeads,
        CancellationToken cancellationToken)
    {
        var leads = await consultaLeads
            .Select(lead => new { lead.Id, lead.CriadoEm })
            .ToListAsync(cancellationToken);

        if (leads.Count == 0)
        {
            return [];
        }

        var idsDosLeads = leads.Select(lead => lead.Id).ToArray();
        var primeiraConversaPorLead = await db.Conversas
            .AsNoTracking()
            .Where(conversa => idsDosLeads.Contains(conversa.LeadId))
            .GroupBy(conversa => conversa.LeadId)
            .Select(grupo => new { LeadId = grupo.Key, CriadaEm = grupo.Min(conversa => conversa.CriadaEm) })
            .ToDictionaryAsync(item => item.LeadId, item => item.CriadaEm, cancellationToken);

        var ultimaMensagemDoLeadPorLead = await (
            from mensagem in db.Mensagens.AsNoTracking()
            join conversa in db.Conversas.AsNoTracking() on mensagem.ConversaId equals conversa.Id
            where mensagem.Papel == Papeis.Lead && idsDosLeads.Contains(conversa.LeadId)
            group mensagem by conversa.LeadId into grupo
            select new { LeadId = grupo.Key, EnviadaEm = grupo.Max(mensagem => mensagem.Em) }
        ).ToDictionaryAsync(item => item.LeadId, item => item.EnviadaEm, cancellationToken);

        var ultimosContatos = new List<(Guid LeadId, DateTimeOffset Em)>();
        foreach (var lead in leads)
        {
            DateTimeOffset ultimoContato;
            if (ultimaMensagemDoLeadPorLead.TryGetValue(lead.Id, out var ultimaMensagemDoLead))
            {
                ultimoContato = ultimaMensagemDoLead;
            }
            else if (primeiraConversaPorLead.TryGetValue(lead.Id, out var primeiraConversa) &&
                     primeiraConversa < lead.CriadoEm)
            {
                ultimoContato = primeiraConversa;
            }
            else
            {
                ultimoContato = lead.CriadoEm;
            }

            ultimosContatos.Add((lead.Id, ultimoContato));
        }

        return ultimosContatos;
    }
}
