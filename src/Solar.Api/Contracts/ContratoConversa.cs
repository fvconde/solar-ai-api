using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

/// <summary>Mensagem que o lead envia na conversa.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NovaMensagemRequest(
    [StringLength(ContratoTurno.LimiteMensagem, MinimumLength = 1)]
    string Texto);

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

/// <summary>Resposta da Lia a um turno, com o perfil ja atualizado.</summary>
public sealed record MensagemResponse(
    Guid ConversaId,
    string Resposta,
    string Intencao,
    string ProximaAcao,
    PerfilLead PerfilLead,
    IReadOnlyList<ImovelSugerido> ImoveisSugeridos,
    string? Corretor,
    bool ContatoPendente);

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
    string? Corretor);

/// <summary>Estado completo de uma conversa.</summary>
public sealed record ConversaResponse(
    Guid ConversaId,
    PerfilLead PerfilLead,
    IReadOnlyList<MensagemDaConversa> Mensagens,
    bool ContatoPendente);
