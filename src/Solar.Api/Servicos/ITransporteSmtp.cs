namespace Solar.Api.Servicos;

public interface ITransporteSmtp
{
    Task EnviarAsync(
        MensagemSmtp mensagem,
        OpcoesSmtpEmail configuracao,
        CancellationToken cancellationToken);
}

public sealed record MensagemSmtp(string Destinatario, string Assunto, string Corpo);
