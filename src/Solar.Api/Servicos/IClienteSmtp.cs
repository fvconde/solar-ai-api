using System.Net;
using System.Net.Mail;

namespace Solar.Api.Servicos;

public interface IFabricaClienteSmtp
{
    IClienteSmtp Criar(string host, int porta);
}

public interface IClienteSmtp : IDisposable
{
    bool EnableSsl { get; set; }
    bool UseDefaultCredentials { get; set; }
    ICredentialsByHost? Credentials { get; set; }

    Task EnviarAsync(MailMessage mensagem, CancellationToken cancellationToken);
}
