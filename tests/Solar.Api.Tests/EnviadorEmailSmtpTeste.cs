using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

public sealed class EnviadorEmailSmtpTeste
{
    [Fact]
    public async Task Producao_envia_recuperacao_aprovacao_e_recusa_com_opcoes_smtp_e_starttls()
    {
        var opcoes = CarregarOpcoesSmtp();
        var fabrica = new FabricaClienteSmtpFalsa();
        var logger = new LoggerDeTeste();
        var enviador = CriarEnviador("Production", opcoes, fabrica, logger);
        var linkRecuperacao = "https://solar.test/entrar?token=token-falso-recuperacao";

        await enviador.EnviarLinkRecuperacaoAsync("recuperacao@example.test", linkRecuperacao);
        await enviador.EnviarAprovacaoAsync(Guid.NewGuid(), "aprovacao@example.test", "Nome Aprovado");
        await enviador.EnviarRecusaAsync(
            Guid.NewGuid(),
            "recusa@example.test",
            "Nome Recusado",
            "Documento incompleto");

        Assert.Equal(3, fabrica.Clientes.Count);
        Assert.Equal("recuperacao@example.test", fabrica.Clientes[0].Mensagens[0].Destinatarios.Single());
        Assert.Equal("Redefinicao de senha Solar", fabrica.Clientes[0].Mensagens[0].Assunto);
        Assert.Contains(linkRecuperacao, fabrica.Clientes[0].Mensagens[0].Corpo);
        Assert.Equal("solar-remetente@example.test", fabrica.Clientes[0].Mensagens[0].Remetente);
        Assert.Equal("aprovacao@example.test", fabrica.Clientes[1].Mensagens[0].Destinatarios.Single());
        Assert.Contains("Nome Aprovado", fabrica.Clientes[1].Mensagens[0].Corpo);
        Assert.Equal("recusa@example.test", fabrica.Clientes[2].Mensagens[0].Destinatarios.Single());
        Assert.Contains("Nome Recusado", fabrica.Clientes[2].Mensagens[0].Corpo);
        Assert.Contains("Documento incompleto", fabrica.Clientes[2].Mensagens[0].Corpo);

        foreach (var cliente in fabrica.Clientes)
        {
            Assert.Equal("smtp.gmail.com", cliente.Host);
            Assert.Equal(587, cliente.Porta);
            Assert.True(cliente.EnableSsl);
            Assert.False(cliente.UseDefaultCredentials);
            var credenciais = Assert.IsType<NetworkCredential>(cliente.Credentials);
            Assert.Equal("conta-smtp@example.test", credenciais.UserName);
            Assert.Equal("senha-app-falsa-nao-secreta", credenciais.Password);
        }

        Assert.Empty(logger.Mensagens);
    }

    [Fact]
    public async Task Development_preserva_os_tres_logs_e_nao_acessa_o_transporte()
    {
        var fabrica = new FabricaClienteSmtpFalsa();
        var logger = new LoggerDeTeste();
        var enviador = CriarEnviador("Development", new OpcoesSmtpEmail(), fabrica, logger);
        var contaId = Guid.Parse("b10af5bc-475a-4fbf-9215-f96aa0681f70");
        const string link = "https://solar.local/entrar?token=token-local";

        await enviador.EnviarLinkRecuperacaoAsync("cliente@example.test", link);
        await enviador.EnviarAprovacaoAsync(contaId, "corretor@example.test", "Nome local");
        await enviador.EnviarRecusaAsync(contaId, "corretor@example.test", "Nome local", "Motivo local");

        Assert.Empty(fabrica.Clientes);
        Assert.Equal(
            new[]
            {
                $"Link de redefinicao de senha: {link}",
                $"E-mail enviado para a conta {contaId}",
                $"E-mail enviado para a conta {contaId}",
            },
            logger.Mensagens);
    }

    [Fact]
    public async Task Erros_de_transporte_nao_expoem_destinatario_conteudo_ou_senha()
    {
        var opcoes = CarregarOpcoesSmtp();
        var fabrica = new FabricaClienteSmtpFalsa((cliente, mensagem, _) =>
        {
            var senha = Assert.IsType<NetworkCredential>(cliente.Credentials).Password;
            var dados = $"{mensagem.Destinatarios.Single()}|{mensagem.Corpo}|{senha}";
            return Task.FromException(new InvalidOperationException(
                dados,
                new InvalidOperationException(dados)));
        });
        var logger = new LoggerDeTeste();
        var enviador = CriarEnviador("Production", opcoes, fabrica, logger);
        const string destinatario = "destinatario-sentinela@example.test";
        const string link = "https://solar.test/entrar?token=TOKEN_SENTINELA_FALSO";
        const string nome = "NOME_SENTINELA_FALSO";
        const string motivo = "MOTIVO_SENTINELA_FALSO";

        var erroRecuperacao = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enviador.EnviarLinkRecuperacaoAsync(destinatario, link));
        var erroRecusa = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enviador.EnviarRecusaAsync(Guid.NewGuid(), destinatario, nome, motivo));

        Assert.Equal("Nao foi possivel enviar o e-mail.", erroRecuperacao.Message);
        Assert.Equal("Nao foi possivel enviar o e-mail.", erroRecusa.Message);
        Assert.Null(erroRecuperacao.InnerException);
        Assert.Null(erroRecusa.InnerException);
        Assert.Empty(logger.Mensagens);
        foreach (var sentinela in new[]
                 {
                     destinatario,
                     "TOKEN_SENTINELA_FALSO",
                     nome,
                     motivo,
                     opcoes.SenhaApp,
                 })
        {
            Assert.DoesNotContain(sentinela, erroRecuperacao.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(sentinela, erroRecusa.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancelamento_do_transporte_permanece_cancelamento_sem_detalhes_sensiveis()
    {
        var opcoes = CarregarOpcoesSmtp();
        using var cancelamento = new CancellationTokenSource();
        var fabrica = new FabricaClienteSmtpFalsa((_, _, token) =>
        {
            Assert.Equal(cancelamento.Token, token);
            cancelamento.Cancel();
            return Task.FromException(new OperationCanceledException(
                "SENTINELA_DE_CANCELAMENTO",
                new Exception("SENTINELA_DE_INNER_EXCEPTION"),
                token));
        });
        var enviador = CriarEnviador("Production", opcoes, fabrica, new LoggerDeTeste());

        var erro = await Assert.ThrowsAsync<OperationCanceledException>(
            () => enviador.EnviarLinkRecuperacaoAsync(
                "cancelamento@example.test",
                "https://solar.test/entrar?token=TOKEN_CANCELADO",
                cancelamento.Token));

        Assert.Equal(cancelamento.Token, erro.CancellationToken);
        Assert.Null(erro.InnerException);
        Assert.DoesNotContain("SENTINELA", erro.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cancelamento@example.test", erro.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_CANCELADO", erro.ToString(), StringComparison.Ordinal);
        Assert.Single(fabrica.Clientes);
    }

    [Fact]
    public async Task Configuracao_que_desabilita_starttls_falha_fechada()
    {
        var opcoes = CarregarOpcoesSmtp();
        opcoes.StartTls = false;
        var fabrica = new FabricaClienteSmtpFalsa();
        var enviador = CriarEnviador("Production", opcoes, fabrica, new LoggerDeTeste());

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(
            () => enviador.EnviarAprovacaoAsync(Guid.NewGuid(), "conta@example.test", "Nome"));

        Assert.Equal("Nao foi possivel enviar o e-mail.", erro.Message);
        Assert.Null(erro.InnerException);
        Assert.Empty(fabrica.Clientes);
    }

    private static OpcoesSmtpEmail CarregarOpcoesSmtp()
    {
        var secao = OpcoesSmtpEmail.SecaoConfiguracao;
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{secao}:Host"] = "smtp.gmail.com",
                [$"{secao}:Porta"] = "587",
                [$"{secao}:StartTls"] = "true",
                [$"{secao}:Usuario"] = "conta-smtp@example.test",
                [$"{secao}:SenhaApp"] = "senha-app-falsa-nao-secreta",
                [$"{secao}:Remetente"] = "solar-remetente@example.test",
            })
            .Build();

        return configuracao.GetSection(secao).Get<OpcoesSmtpEmail>()
            ?? throw new InvalidOperationException("Configuracao de teste ausente.");
    }

    private static EnviadorEmail CriarEnviador(
        string ambiente,
        OpcoesSmtpEmail opcoes,
        FabricaClienteSmtpFalsa fabrica,
        ILogger<EnviadorEmail> logger) =>
        new(new AmbienteTeste(ambiente), logger, Options.Create(opcoes), new TransporteSmtp(fabrica));

    private sealed class FabricaClienteSmtpFalsa(
        Func<ClienteSmtpFalso, MensagemCapturada, CancellationToken, Task>? aoEnviar = null)
        : IFabricaClienteSmtp
    {
        public List<ClienteSmtpFalso> Clientes { get; } = [];

        public IClienteSmtp Criar(string host, int porta)
        {
            var cliente = new ClienteSmtpFalso(aoEnviar, host, porta);
            Clientes.Add(cliente);
            return cliente;
        }
    }

    private sealed class ClienteSmtpFalso(
        Func<ClienteSmtpFalso, MensagemCapturada, CancellationToken, Task>? aoEnviar,
        string host,
        int porta) : IClienteSmtp
    {
        public string Host { get; } = host;
        public int Porta { get; } = porta;
        public bool EnableSsl { get; set; }
        public bool UseDefaultCredentials { get; set; }
        public ICredentialsByHost? Credentials { get; set; }
        public List<MensagemCapturada> Mensagens { get; } = [];

        public Task EnviarAsync(MailMessage mensagem, CancellationToken cancellationToken)
        {
            var capturada = new MensagemCapturada(
                mensagem.From?.Address ?? string.Empty,
                mensagem.To.Select(destinatario => destinatario.Address).ToArray(),
                mensagem.Subject,
                mensagem.Body);
            Mensagens.Add(capturada);
            return aoEnviar?.Invoke(this, capturada, cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class LoggerDeTeste : ILogger<EnviadorEmail>
    {
        public List<string> Mensagens { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Mensagens.Add(formatter(state, exception));
        }
    }

    private sealed class AmbienteTeste(string ambiente) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = ambiente;
        public string ApplicationName { get; set; } = "Solar.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record MensagemCapturada(
        string Remetente,
        string[] Destinatarios,
        string Assunto,
        string Corpo);
}
