using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

/// <summary>Mensagem que o lead envia na conversa.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NovaMensagemRequest(
    [StringLength(ContratoTurno.LimiteMensagem, MinimumLength = 1)]
    string Texto);

/// <summary>Resposta da Lia a um turno, com o perfil ja atualizado.</summary>
public sealed record MensagemResponse(
    Guid ConversaId,
    string Resposta,
    string Intencao,
    string ProximaAcao,
    PerfilLead PerfilLead,
    IReadOnlyList<ImovelSugerido> ImoveisSugeridos);

/// <summary>Estado completo de uma conversa.</summary>
public sealed record ConversaResponse(
    Guid ConversaId,
    PerfilLead PerfilLead,
    IReadOnlyList<MensagemHistorico> Mensagens);
