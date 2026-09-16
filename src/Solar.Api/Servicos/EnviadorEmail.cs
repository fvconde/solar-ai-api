namespace Solar.Api.Servicos;

/// <summary>
/// Implementacao deliberadamente sem SMTP neste card. Em Development o link
/// aparece no log para o fluxo local; nos demais ambientes a abstracao fica
/// silenciosa ate existir um provedor aprovado.
/// </summary>
public sealed class EnviadorEmail(
    IHostEnvironment ambiente,
    ILogger<EnviadorEmail> logger) : IEnviadorEmail
{
    public Task EnviarLinkRecuperacaoAsync(
        string destinatario,
        string link,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ambiente.IsDevelopment())
        {
            // O destinatario nao e incluido: o unico dado sensivel permitido
            // nesta linha e o proprio link de reset solicitado pelo handoff.
            logger.LogInformation("Link de redefinicao de senha: {Link}", link);
        }

        return Task.CompletedTask;
    }
}
