using System.Net;
using System.Net.Mail;
using System.Text;

namespace Solar.Api.Servicos;

public sealed class TransporteSmtp(IFabricaClienteSmtp fabricaCliente) : ITransporteSmtp
{
    public async Task EnviarAsync(
        MensagemSmtp mensagem,
        OpcoesSmtpEmail configuracao,
        CancellationToken cancellationToken)
    {
        using var cliente = fabricaCliente.Criar(configuracao.Host, configuracao.Porta);
        cliente.EnableSsl = configuracao.StartTls;
        cliente.UseDefaultCredentials = false;
        cliente.Credentials = new NetworkCredential(configuracao.Usuario, configuracao.SenhaApp);

        using var email = new MailMessage
        {
            From = new MailAddress(configuracao.Remetente),
            Subject = mensagem.Assunto,
            SubjectEncoding = Encoding.UTF8,
            Body = mensagem.Corpo,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        email.To.Add(new MailAddress(mensagem.Destinatario));

        await cliente.EnviarAsync(email, cancellationToken);
    }
}
