namespace Solar.Api.Contracts;

public sealed record ValidacaoResponse(
    string Codigo,
    IReadOnlyDictionary<string, string> Campos);

public sealed record ErroApiResponse(string Codigo, string Mensagem);
