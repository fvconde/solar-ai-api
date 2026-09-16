using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

/// <summary>Insumos persistidos que o agente usa para resumir o encaminhamento.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResumoRequest(
    PerfilLead PerfilLead,
    [MinLength(1), MaxLength(ContratoTurno.LimiteHistorico)]
    IReadOnlyList<MensagemHistorico> Historico,
    [MaxLength(ContratoTurno.LimiteImoveis)]
    IReadOnlyList<ImovelSugerido> Imoveis);

/// <summary>Cinco secoes curtas; nulo significa que a conversa nao deu base.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResumoResponse(
    string? Perfil,
    string? Orcamento,
    string? Imoveis,
    string? Objecoes,
    string? ProximoPasso);
