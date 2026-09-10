using Solar.Api.Contracts;

namespace Solar.Api.Dominio;

public static class Canais
{
    public const string Web = "web";
    public const string Telegram = "telegram";
}

/// <summary>
/// Liga um <see cref="Dominio.Lead"/> a um canal e guarda o que foi falado.
/// O <see cref="Id"/> vem do cliente: a conversa nasce no primeiro POST.
/// </summary>
public sealed class Conversa
{
    private readonly List<Mensagem> _mensagens = [];

    private Conversa()
    {
    }

    public Guid Id { get; private set; }

    public string Canal { get; private set; } = Canais.Web;

    public Guid LeadId { get; private set; }

    public Lead Lead { get; private set; } = null!;

    public DateTimeOffset CriadaEm { get; private set; }

    public DateTimeOffset AtualizadaEm { get; private set; }

    public IReadOnlyList<Mensagem> Mensagens => _mensagens;

    public static Conversa Nova(Guid id, string canal, DateTimeOffset em)
    {
        var lead = Lead.Novo(em);

        return new Conversa
        {
            Id = id,
            Canal = canal,
            Lead = lead,
            LeadId = lead.Id,
            CriadaEm = em,
            AtualizadaEm = em,
        };
    }

    /// <summary>Repassa esta conversa ao lead canonico depois da dedupe por contato.</summary>
    public void ReapontarLead(Lead canonico)
    {
        Lead = canonico;
        LeadId = canonico.Id;
    }

    /// <summary>
    /// Grava as duas falas do turno e funde o perfil. So roda depois de o agente
    /// ter respondido: turno que falha nao deixa rastro.
    /// </summary>
    public void RegistrarTurno(string mensagemDoLead, TurnoResponse turno, DateTimeOffset em)
    {
        _mensagens.Add(Mensagem.DoLead(Id, mensagemDoLead, em));
        _mensagens.Add(Mensagem.DaLia(Id, turno.Resposta, turno.ProximaAcao, em));

        Lead.Fundir(turno.Intencao, turno.CamposExtraidos, em);

        AtualizadaEm = em;
    }
}
