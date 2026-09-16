namespace Solar.Api.Servicos;

public interface IEnviadorEmail
{
    Task EnviarLinkRecuperacaoAsync(
        string destinatario,
        string link,
        CancellationToken cancellationToken = default);
}
