using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

/// <summary>Mensagem que o lead envia na conversa.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NovaMensagemRequest(
    [StringLength(ContratoTurno.LimiteMensagem, MinimumLength = 1)]
    string Texto);

public static class AvisoPrivacidade
{
    public const int LimiteVersao = 40;
    public const string VersaoAtual = "2026-09-11";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConsentimentoRequest(
    [StringLength(AvisoPrivacidade.LimiteVersao, MinimumLength = 1)]
    string VersaoAvisoPrivacidade);

public sealed record ConsentimentoResponse(
    Guid ConversaId,
    Guid LeadId,
    DateTimeOffset ConsentimentoEm,
    string VersaoAvisoPrivacidade);

/// <summary>Contato que o lead informa no handoff.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContatoRequest(
    [StringLength(ContratoContato.LimiteNome)] string? Nome,
    [StringLength(ContratoContato.LimiteTelefone)] string? Telefone,
    [EmailAddress][StringLength(ContratoContato.LimiteEmail)] string? Email);

/// <summary>
/// So o identificador do lead. Devolver o nome gravado contaria a quem digitasse
/// um telefone alheio de quem ele e.
/// </summary>
public sealed record ContatoResponse(Guid LeadId);

public static class EstadosDoAgendamento
{
    public const string Confirmado = "confirmado";
    public const string Indisponivel = "indisponivel";
}

/// <summary>Fato do banco exibido como evento, separado da fala da Lia.</summary>
public sealed record AgendamentoDaConversa(
    string Estado,
    SlotOferecido? Horario,
    IReadOnlyList<SlotOferecido> Alternativas);

/// <summary>Resposta da Lia a um turno, com o perfil ja atualizado.</summary>
public sealed record MensagemResponse(
    Guid ConversaId,
    string Resposta,
    string Intencao,
    string ProximaAcao,
    PerfilLead PerfilLead,
    IReadOnlyList<ImovelSugerido> ImoveisSugeridos,
    string? Corretor,
    bool ContatoPendente,
    AgendamentoDaConversa? Agendamento);

/// <summary>
/// Uma fala ja gravada, do jeito que a UI precisa para redesenhar a trilha.
///
/// <para>
/// Nao e o <see cref="MensagemHistorico"/>: aquele e o contrato congelado que
/// atravessa a fronteira para o agente, espelhado no Pydantic, e o agente nao
/// tem uso para o desfecho de turnos passados. Este DTO existe so no .NET e
/// pode crescer sem commit coordenado.
/// </para>
/// </summary>
public sealed record MensagemDaConversa(
    string Papel,
    string Texto,
    DateTimeOffset Em,
    string? ProximaAcao,
    string? Corretor,
    AgendamentoDaConversa? Agendamento,
    IReadOnlyList<ImovelSugerido>? ImoveisSugeridos = null);

/// <summary>Estado completo de uma conversa.</summary>
public sealed record ConversaResponse(
    Guid ConversaId,
    PerfilLead PerfilLead,
    IReadOnlyList<MensagemDaConversa> Mensagens,
    bool ContatoPendente,
    DateTimeOffset? ConsentimentoEm,
    string? VersaoAvisoPrivacidade);

/// <summary>Resultado da exclusao de dados do lead.</summary>
public sealed record ExclusaoLeadResultado(
    Guid LeadId,
    int ConversasAfetadas,
    int MensagensExcluidas,
    DateTimeOffset RemovidoEm);

/// <summary>Confirmacao de exclusao dos dados do lead por solicitacao LGPD.</summary>
public sealed record ExclusaoLeadResponse(
    Guid LeadId,
    int ConversasAfetadas,
    int MensagensExcluidas,
    DateTimeOffset RemovidoEm,
    string Escopo = "lead_e_vinculos",
    string Mensagem = "Dados do lead e registros vinculados foram eliminados definitivamente.");

/// <summary>Resultado interno da exclusao de conversa.</summary>
public sealed record ExclusaoConversaResultado(
    Guid ConversaId,
    Guid LeadId,
    bool LeadExcluido,
    int MensagensExcluidas,
    DateTimeOffset RemovidoEm,
    string Escopo);

/// <summary>Confirmacao de exclusao de conversa, com escopo e efeito no lead explicitados.</summary>
public sealed record ExclusaoConversaResponse(
    Guid ConversaId,
    Guid LeadId,
    bool LeadExcluido,
    int MensagensExcluidas,
    DateTimeOffset RemovidoEm,
    string Escopo,
    string Mensagem);
