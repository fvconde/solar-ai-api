namespace Solar.Api.Servicos;

public sealed class OpcoesSmtpEmail
{
    public const string SecaoConfiguracao = "Email:Smtp";

    public string Host { get; set; } = "smtp.gmail.com";
    public int Porta { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string Usuario { get; set; } = string.Empty;
    public string SenhaApp { get; set; } = string.Empty;
    public string Remetente { get; set; } = string.Empty;

    internal void Validar()
    {
        if (string.IsNullOrWhiteSpace(Host)
            || Host.Any(char.IsWhiteSpace)
            || Porta is < 1 or > 65535
            || !StartTls
            || string.IsNullOrWhiteSpace(Usuario)
            || string.IsNullOrWhiteSpace(SenhaApp)
            || string.IsNullOrWhiteSpace(Remetente))
        {
            throw new InvalidOperationException("A configuracao SMTP e invalida.");
        }
    }
}
