namespace Solar.Api.Dominio;

public static class StatusDoEncaminhamento
{
    public const string Atribuido = "atribuido";
    public const string Aguardando = "aguardando";
}

/// <summary>
/// O momento em que a conversa deixa de ser da Lia e passa a ser de um corretor.
/// Uma linha por conversa.
/// </summary>
public sealed class Encaminhamento
{
    private Encaminhamento()
    {
    }

    public long Id { get; private set; }

    public Guid ConversaId { get; private set; }

    public Guid LeadId { get; private set; }

    public Guid? CorretorId { get; private set; }

    public Corretor? Corretor { get; private set; }

    public string Especialidade { get; private set; } = Especialidades.Moradia;

    public string Status { get; private set; } = StatusDoEncaminhamento.Aguardando;

    public DateTimeOffset Em { get; private set; }

    public void ReapontarLead(Guid canonicoId) => LeadId = canonicoId;

    public static Encaminhamento Novo(
        Guid conversaId,
        Guid leadId,
        Guid? corretorId,
        string especialidade,
        DateTimeOffset em) => new()
        {
            ConversaId = conversaId,
            LeadId = leadId,
            CorretorId = corretorId,
            Especialidade = especialidade,
            Status = corretorId is null
                ? StatusDoEncaminhamento.Aguardando
                : StatusDoEncaminhamento.Atribuido,
            Em = em,
        };
}
