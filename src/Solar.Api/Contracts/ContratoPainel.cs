namespace Solar.Api.Contracts;

/// <summary>Identificação pública de um corretor ativo para o gate de acesso do painel.</summary>
public sealed record CorretorIdentificacao(
    Guid Id,
    string Nome,
    string Especialidade);

/// <summary>Item da fila de leads exibido na tela do corretor.</summary>
public sealed record LeadPainelItem(
    Guid Id,
    string? Nome,
    string? Intencao,
    int? Score,
    DateTimeOffset UltimaInteracao,
    string Status,
    Guid? CorretorId,
    string? CorretorNome,
    string? Telefone,
    string? Email,
    string? Regiao);

/// <summary>Resposta da listagem da fila de leads com contagem total.</summary>
public sealed record FilaLeadsResponse(
    IReadOnlyList<LeadPainelItem> Leads,
    int Total);
