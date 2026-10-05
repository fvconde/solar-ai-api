using System.Net;
using System.Net.Mail;

namespace Solar.Api.Servicos;

public sealed class FabricaClienteSmtp : IFabricaClienteSmtp
{
    public IClienteSmtp Criar(string host, int porta)
    {
        var cliente = new SmtpClient(host, porta)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        return new ClienteSmtp(cliente);
    }

    private sealed class ClienteSmtp(SmtpClient cliente) : IClienteSmtp
    {
        public bool EnableSsl
        {
            get => cliente.EnableSsl;
            set => cliente.EnableSsl = value;
        }

        public bool UseDefaultCredentials
        {
            get => cliente.UseDefaultCredentials;
            set => cliente.UseDefaultCredentials = value;
        }

        public ICredentialsByHost? Credentials
        {
            get => cliente.Credentials;
            set => cliente.Credentials = value;
        }

        public Task EnviarAsync(MailMessage mensagem, CancellationToken cancellationToken) =>
            cliente.SendMailAsync(mensagem, cancellationToken);

        public void Dispose() => cliente.Dispose();
    }
}
