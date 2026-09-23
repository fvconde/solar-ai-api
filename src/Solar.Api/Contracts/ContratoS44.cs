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

public sealed record ValidacaoResponse(
    string Codigo,
    IReadOnlyDictionary<string, string> Campos);

public sealed record ErroApiResponse(string Codigo, string Mensagem);
