namespace Solar.Api.Contracts;

public sealed record CadastroContaRequest(
    string? Nome,
    string? Email,
    string? Telefone,
    string? Senha,
    bool? AceitePrivacidade,
    Guid? ConversaId);

public sealed record CadastroCorretorRequest(
    string? Nome,
    string? Email,
    string? Telefone,
    string? Senha,
    bool? AceitePrivacidade,
    IReadOnlyList<string>? Regioes,
    IReadOnlyList<string>? Especialidades);

public sealed record CorretorContaResponse(
    string Status,
    IReadOnlyList<string> Regioes,
    IReadOnlyList<string> Especialidades,
    DateTimeOffset? AprovadoEm);

public sealed record ConsentimentoContaResponse(DateTimeOffset Em, string Versao);

public sealed record ContaResponse(
    Guid Id,
    string Nome,
    string Email,
    string Telefone,
    string Perfil,
    DateTimeOffset CriadaEm,
    CorretorContaResponse? Corretor,
    ConsentimentoContaResponse? Consentimento,
    int ConversasSalvas);

public sealed record AtualizarContaRequest(
    string? Nome,
    string? Telefone,
    string? Email,
    string? SenhaAtual,
    IReadOnlyList<string>? Regioes,
    IReadOnlyList<string>? Especialidades);

public sealed record AlterarSenhaContaRequest(string? SenhaAtual, string? NovaSenha);

public sealed record ExcluirContaRequest(string? Email);
