using Solar.Api.Dominio;

namespace Solar.Api.Encaminhamentos;

internal static class RegrasDaFilaDeLeads
{
    public static Dictionary<Guid, Encaminhamento> SelecionarUltimoEncaminhamentoPorLead(
        IEnumerable<Encaminhamento> encaminhamentos)
    {
        return encaminhamentos
            .GroupBy(e => e.LeadId)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo
                    .OrderByDescending(e => e.Em)
                    .ThenByDescending(e => e.Id)
                    .First());
    }

    public static bool PertenceAoFiltro(
        Lead lead,
        Encaminhamento? encaminhamento,
        string filtro,
        Guid? corretorId)
    {
        return filtro switch
        {
            "meus_leads" or "minha_fila" =>
                corretorId is not null && encaminhamento?.CorretorId == corretorId,
            "sem_corretor" =>
                (lead.Status == StatusDoLead.Novo && encaminhamento is null) ||
                encaminhamento?.CorretorId is null,
            "visao_geral" => true,
            _ => false,
        };
    }
}
