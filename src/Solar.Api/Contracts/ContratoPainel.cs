namespace Solar.Api.Contracts;

/// <summary>Identificação de um corretor ativo para a seleção de perfil no painel.</summary>
public sealed record CorretorIdentificacao(
    Guid Id,
    string Nome,
    string Especialidade);

/// <summary>
/// Item da fila de leads exibido na tela do corretor.
/// Em estrita conformidade com a LGPD e com os critérios de aceite do S-20,
/// dados de contato direto (telefone, e-mail) não trafegam na listagem da fila,
/// ficando restritos ao escopo de detalhe individual do lead (S-21).
/// </summary>
public sealed record LeadPainelItem(
    Guid Id,
    string? Nome,
    string? Intencao,
    int? Score,
    DateTimeOffset UltimaInteracao,
    string Status,
    Guid? CorretorId,
    string? CorretorNome,
    string? Regiao);

/// <summary>Resposta da listagem da fila de leads com contagem total.</summary>
public sealed record FilaLeadsResponse(
    IReadOnlyList<LeadPainelItem> Leads,
    int Total);
public sealed record IdentificacaoPainelRequest(string? Email);

public sealed record IdentificacaoPainelResponse(bool Cadastrado);

public sealed record SessaoPainelRequest(string? Email, string? Senha);

public sealed record CorretorPainelResponse(Guid Id, string Nome, string Especialidade);

public sealed record SessaoPainelResponse(CorretorPainelResponse Corretor);

public sealed record TentativasRestantesResponse(int TentativasRestantes);

public sealed record BloqueadoPorSegundosResponse(int BloqueadoPorSegundos);

public sealed record RecuperacaoSenhaRequest(string? Email);

public sealed record RecuperacaoSenhaTokenResponse(string Email);

public sealed record NovaSenhaPainelRequest(string? Token, string? NovaSenha);

public sealed record ErroPainelResponse(string Erro);
