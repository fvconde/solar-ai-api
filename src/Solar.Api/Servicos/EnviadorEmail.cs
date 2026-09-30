using Microsoft.Extensions.Options;

namespace Solar.Api.Servicos;

/// <summary>
/// Preserva os logs locais de Development e envia por SMTP nos demais ambientes.
/// </summary>
public sealed class EnviadorEmail(
    IHostEnvironment ambiente,
    ILogger<EnviadorEmail> logger,
    IOptions<OpcoesSmtpEmail> opcoesSmtp,
    ITransporteSmtp transporteSmtp) : IEnviadorEmail
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
            return Task.CompletedTask;
        }

        return EnviarComSmtpAsync(
            destinatario,
            "Redefinicao de senha Solar",
            $"Use o link a seguir para redefinir sua senha: {link}",
            cancellationToken);
    }

    public Task EnviarAprovacaoAsync(
        Guid contaId,
        string destinatario,
        string nome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ambiente.IsDevelopment())
        {
            logger.LogInformation("E-mail enviado para a conta {ContaId}", contaId);
            return Task.CompletedTask;
        }

        return EnviarComSmtpAsync(
            destinatario,
            "Cadastro aprovado",
            $"Ola {nome}, seu cadastro foi aprovado.",
            cancellationToken);
    }

    public Task EnviarRecusaAsync(
        Guid contaId,
        string destinatario,
        string nome,
        string? motivo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ambiente.IsDevelopment())
        {
            logger.LogInformation("E-mail enviado para a conta {ContaId}", contaId);
            return Task.CompletedTask;
        }

        var corpo = string.IsNullOrWhiteSpace(motivo)
            ? $"Ola {nome}, seu cadastro nao foi aprovado."
            : $"Ola {nome}, seu cadastro nao foi aprovado.{Environment.NewLine}Motivo informado: {motivo}";

        return EnviarComSmtpAsync(destinatario, "Cadastro nao aprovado", corpo, cancellationToken);
    }

    private async Task EnviarComSmtpAsync(
        string destinatario,
        string assunto,
        string corpo,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuracao = opcoesSmtp.Value;
            configuracao.Validar();
            await transporteSmtp.EnviarAsync(
                new MensagemSmtp(destinatario, assunto, corpo),
                configuracao,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            // Excecoes SMTP podem conter destinatario ou resposta do servidor; nao as propague.
            throw new InvalidOperationException("Nao foi possivel enviar o e-mail.");
        }
    }
}
