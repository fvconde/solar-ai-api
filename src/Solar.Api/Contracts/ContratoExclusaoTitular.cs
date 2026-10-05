namespace Solar.Api.Contracts;

public enum EstadoExclusaoTitular
{
    Excluida,
    NaoEncontrada,
    NaoAutorizada,
    Conflito,
}

public sealed record ExclusaoTitularResultado(
    EstadoExclusaoTitular Estado,
    bool LeadExcluido = false,
    DateTimeOffset? RemovidoEm = null);

public sealed record ExclusaoTitularResponse(
    bool LeadExcluido,
    DateTimeOffset RemovidoEm,
    string Escopo,
    string Mensagem);
