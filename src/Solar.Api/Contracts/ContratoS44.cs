namespace Solar.Api.Contracts;

public sealed record UsuarioResponse(Guid Id, string Nome, string Email);

public sealed record SessaoResponse(
    UsuarioResponse Usuario,
    string Perfil,
    string? StatusCorretor,
    Guid? CorretorId,
    bool VinculoAtivo,
    IReadOnlyList<string> FiltrosPermitidos,
    string? FiltroInicial,
    int? PendentesAprovacao);

public sealed record SessaoRequest(string? Email, string? Senha, Guid? ConversaId);

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

public sealed record ConversaResumo(Guid Id, string Titulo, DateTimeOffset AtualizadaEm, string Estado);

public sealed record ValidacaoResponse(
    string Codigo,
    IReadOnlyDictionary<string, string> Campos);

public sealed record ErroApiResponse(string Codigo, string Mensagem);
