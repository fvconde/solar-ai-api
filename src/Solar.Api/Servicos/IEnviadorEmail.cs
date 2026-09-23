namespace Solar.Api.Servicos;

public interface IEnviadorEmail
{
    Task EnviarLinkRecuperacaoAsync(
        string destinatario,
        string link,
        CancellationToken cancellationToken = default);

    Task EnviarAprovacaoAsync(
        Guid contaId,
        string destinatario,
        string nome,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task EnviarRecusaAsync(
        Guid contaId,
        string destinatario,
        string nome,
        string? motivo,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
