using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class ForwardedHeadersHttpTeste : IClassFixture<ProxyTrustApiFactory>
{
    private readonly HttpClient cliente;

    public ForwardedHeadersHttpTeste(ProxyTrustApiFactory fabrica)
    {
        cliente = fabrica.CreateClient();
    }

    [Fact]
    public async Task IP_do_cliente_e_lido_de_cadeia_que_termina_em_proxies_confiaveis()
    {
        using var resposta = await SolicitarAsync(
            "192.0.2.10",
            "192.0.2.200, 203.0.113.10, 198.51.100.10");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.10", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rate_limit_separa_clientes_encaminhados_pelo_mesmo_proxy()
    {
        using var primeiroCliente = await SolicitarAsync("192.0.2.10", "203.0.113.20, 198.51.100.20");
        using var repeticaoDoPrimeiro = await SolicitarAsync("192.0.2.10", "203.0.113.20, 198.51.100.20");
        using var segundoCliente = await SolicitarAsync("192.0.2.10", "203.0.113.21, 198.51.100.21");

        Assert.Equal(HttpStatusCode.OK, primeiroCliente.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeticaoDoPrimeiro.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segundoCliente.StatusCode);
    }

    [Fact]
    public async Task XForwardedFor_e_ignorado_quando_a_allow_list_padrao_esta_vazia()
    {
        using var fabricaSemAllowList = new ProxyTrustApiFactory();
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardLimit", null);
        Environment.SetEnvironmentVariable("ProxyTrust__KnownProxies__0", null);
        Environment.SetEnvironmentVariable("ProxyTrust__KnownIPNetworks__0", null);
        using var clienteSemAllowList = fabricaSemAllowList.CreateClient();
        using var resposta = await SolicitarAsync(
            clienteSemAllowList,
            "203.0.113.91",
            "198.51.100.91, 192.0.2.91");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.91", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task XForwardedFor_de_peer_desconhecido_e_ignorado()
    {
        using var resposta = await SolicitarAsync(
            "203.0.113.90",
            "198.51.100.90, 192.0.2.90");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.90", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Prefixo_falso_de_XForwardedFor_nao_altera_o_IP_do_cliente()
    {
        using var resposta = await SolicitarAsync(
            "192.0.2.10",
            "198.51.100.99, 203.0.113.99, 198.51.100.30");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.99", await resposta.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public async Task Rede_de_proxies_com_prefixo_zero_impede_inicializacao(string rede)
    {
        using var fabrica = new ProxyTrustApiFactory();
        Environment.SetEnvironmentVariable("ProxyTrust__KnownIPNetworks__0", rede);
        var excecao = await Record.ExceptionAsync(async () =>
        {
            using var clienteComRedeInvalida = fabrica.CreateClient();
            using var resposta = await clienteComRedeInvalida.GetAsync("/_test/proxy-ip");
        });

        Assert.NotNull(excecao);
        Assert.Contains("ProxyTrust:KnownIPNetworks", excecao.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("-1")]
    public async Task Limite_de_saltos_invalido_impede_inicializacao(string limite)
    {
        using var fabrica = new ProxyTrustApiFactory();
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardLimit", limite);
        var excecao = await Record.ExceptionAsync(async () =>
        {
            using var clienteComLimiteInvalido = fabrica.CreateClient();
            using var resposta = await clienteComLimiteInvalido.GetAsync("/_test/proxy-ip");
        });

        Assert.NotNull(excecao);
        Assert.Contains("ProxyTrust:ForwardLimit", excecao.ToString());
    }

    [Fact]
    public async Task Header_proprio_de_peer_conhecido_define_IP_e_ignora_XFF()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(
            clienteProprio, "192.0.2.10", "203.0.113.70", "198.51.100.70");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.70", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task XFF_sozinho_e_ignorado_quando_configurado_header_proprio()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarAsync(clienteProprio, "192.0.2.10", "203.0.113.71");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("192.0.2.10", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Header_proprio_de_peer_desconhecido_e_ignorado()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(
            clienteProprio, "203.0.113.72", "203.0.113.73");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.72", await resposta.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("SENTINELA_HEADER_INVALIDO")]
    [InlineData("203.0.113.74, 203.0.113.75")]
    [InlineData(" 203.0.113.74")]
    [InlineData("203.0.113.74 ")]
    [InlineData("203.0.113.74:8080")]
    [InlineData("")]
    public async Task Header_proprio_invalido_ou_com_lista_nao_define_IP(string valor)
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(clienteProprio, "192.0.2.10", valor);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("192.0.2.10", await resposta.Content.ReadAsStringAsync());
        Assert.Equal("False", resposta.Headers.GetValues("X-Test-Header-Proprio-Presente").Single());
        Assert.DoesNotContain(fabrica.MensagensDeLog, mensagem => mensagem.Contains("SENTINELA_HEADER_INVALIDO"));
    }

    [Fact]
    public async Task Header_proprio_repetido_em_duas_linhas_e_removido_sem_mudar_peer()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(clienteProprio, "192.0.2.10", null,
            linhas: ["203.0.113.78", "203.0.113.79"]);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("192.0.2.10", await resposta.Content.ReadAsStringAsync());
        Assert.Equal("False", resposta.Headers.GetValues("X-Test-Header-Proprio-Presente").Single());
    }

    [Fact]
    public async Task Header_proprio_com_IPv6_valido_define_IP_normalizado()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(
            clienteProprio, "192.0.2.10", "2001:0db8:0:0::80");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("2001:db8::80", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Header_proprio_ausente_mantem_peer_mesmo_com_XFF()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var resposta = await SolicitarHeaderProprioAsync(
            clienteProprio, "192.0.2.10", null, "203.0.113.81");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("192.0.2.10", await resposta.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Modo_XFF_padrao_preserva_cadeia_e_nao_instala_filtro_do_header_proprio()
    {
        using var resposta = await SolicitarHeaderProprioAsync(cliente, "192.0.2.10",
            "SENTINELA_HEADER_INVALIDO", "203.0.113.82, 198.51.100.82");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("203.0.113.82", await resposta.Content.ReadAsStringAsync());
        Assert.Equal("True", resposta.Headers.GetValues("X-Test-Header-Proprio-Presente").Single());
    }

    [Theory]
    [InlineData("X-Real-IP")]
    [InlineData("X-Solar-Client-IP,X-Forwarded-For")]
    [InlineData(" X-Solar-Client-IP")]
    [InlineData("x-solar-client-ip")]
    public async Task Header_fora_da_allow_list_recusa_boot(string header)
    {
        using var fabrica = new ProxyTrustApiFactory();
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardedForHeaderName", header);
        var excecao = await Record.ExceptionAsync(async () =>
        {
            using var clienteInvalido = fabrica.CreateClient();
            using var resposta = await clienteInvalido.GetAsync("/_test/proxy-ip");
        });

        Assert.NotNull(excecao);
        Assert.Contains("ProxyTrust:ForwardedForHeaderName", excecao.ToString());
    }

    [Fact]
    public async Task Rate_limit_separa_clientes_pelo_header_proprio()
    {
        using var fabrica = CriarFabricaComHeaderProprio();
        using var clienteProprio = fabrica.CreateClient();
        using var primeiro = await SolicitarHeaderProprioAsync(clienteProprio, "192.0.2.10", "203.0.113.76");
        using var repeticao = await SolicitarHeaderProprioAsync(clienteProprio, "192.0.2.10", "203.0.113.76");
        using var segundo = await SolicitarHeaderProprioAsync(clienteProprio, "192.0.2.10", "203.0.113.77");

        Assert.Equal(HttpStatusCode.OK, primeiro.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, repeticao.StatusCode);
        Assert.Equal(HttpStatusCode.OK, segundo.StatusCode);
    }

    private static ProxyTrustApiFactory CriarFabricaComHeaderProprio()
    {
        var fabrica = new ProxyTrustApiFactory();
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardedForHeaderName", "X-Solar-Client-IP");
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardLimit", "1");
        Environment.SetEnvironmentVariable("ProxyTrust__KnownIPNetworks__0", null);
        return fabrica;
    }

    private static async Task<HttpResponseMessage> SolicitarHeaderProprioAsync(
        HttpClient clienteAlvo, string peerImediato, string? valor, string? xForwardedFor = null,
        string[]? linhas = null)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/_test/proxy-ip");
        requisicao.Headers.TryAddWithoutValidation("X-Test-Remote-IP", peerImediato);
        if (linhas is not null)
        {
            requisicao.Headers.TryAddWithoutValidation("X-Solar-Client-IP", linhas);
        }
        else if (valor is not null)
        {
            requisicao.Headers.TryAddWithoutValidation("X-Solar-Client-IP", valor);
        }
        if (xForwardedFor is not null)
        {
            requisicao.Headers.TryAddWithoutValidation("X-Forwarded-For", xForwardedFor);
        }

        return await clienteAlvo.SendAsync(requisicao);
    }

    private async Task<HttpResponseMessage> SolicitarAsync(string peerImediato, string xForwardedFor)
        => await SolicitarAsync(cliente, peerImediato, xForwardedFor);

    private static async Task<HttpResponseMessage> SolicitarAsync(
        HttpClient clienteAlvo,
        string peerImediato,
        string xForwardedFor)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/_test/proxy-ip");
        requisicao.Headers.TryAddWithoutValidation("X-Test-Remote-IP", peerImediato);
        requisicao.Headers.TryAddWithoutValidation("X-Forwarded-For", xForwardedFor);
        return await clienteAlvo.SendAsync(requisicao);
    }
}

[ApiController]
[Route("_test/proxy-ip")]
public sealed class ProxyAddressProbeController : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting("mensagens")]
    public IActionResult Get()
    {
        Response.Headers["X-Test-Header-Proprio-Presente"] = Request.Headers.ContainsKey("X-Solar-Client-IP").ToString();
        return Content(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
    }
}

public sealed class ProxyTrustApiFactory : WebApplicationFactory<Program>
{
    private static readonly string[] VariaveisFixasDeTeste =
    [
        "ConnectionStrings__Postgres",
        "RateLimiting__MensagensPorMinuto",
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
    ];

    private readonly Dictionary<string, string?> valoresAnteriores;
    public ConcurrentQueue<string> MensagensDeLog { get; } = new();

    public ProxyTrustApiFactory()
    {
        var variaveisProxyAnteriores = Environment.GetEnvironmentVariables()
            .Keys
            .OfType<string>()
            .Where(nome => nome.Equals("ProxyTrust", StringComparison.OrdinalIgnoreCase)
                || nome.StartsWith("ProxyTrust__", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        valoresAnteriores = VariaveisFixasDeTeste
            .Concat(variaveisProxyAnteriores)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(nome => nome, nome => Environment.GetEnvironmentVariable(nome), StringComparer.OrdinalIgnoreCase);

        foreach (var nome in variaveisProxyAnteriores)
        {
            Environment.SetEnvironmentVariable(nome, null);
        }

        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres",
            PostgresTestDatabase.ObterConexaoParaAplicacao());
        Environment.SetEnvironmentVariable("RateLimiting__MensagensPorMinuto", "1");
        Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", null);

        Environment.SetEnvironmentVariable("ProxyTrust__ForwardLimit", "2");
        Environment.SetEnvironmentVariable("ProxyTrust__KnownProxies__0", "192.0.2.10");
        Environment.SetEnvironmentVariable("ProxyTrust__KnownIPNetworks__0", "198.51.100.0/24");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturaDeLogs(MensagensDeLog)));
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ProxyAddressProbeController).Assembly);
            services.AddTransient<IStartupFilter, ProxyRemoteAddressStartupFilter>();
        });
    }

    private sealed class CapturaDeLogs(ConcurrentQueue<string> mensagens) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new LoggerDeTeste(mensagens);
        public void Dispose() { }

        private sealed class LoggerDeTeste(ConcurrentQueue<string> mensagens) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                mensagens.Enqueue(formatter(state, exception));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var variaveisProxyAtuais = Environment.GetEnvironmentVariables()
                .Keys
                .OfType<string>()
                .Where(nome => nome.Equals("ProxyTrust", StringComparison.OrdinalIgnoreCase)
                    || nome.StartsWith("ProxyTrust__", StringComparison.OrdinalIgnoreCase));
            foreach (var nome in variaveisProxyAtuais)
            {
                Environment.SetEnvironmentVariable(nome, null);
            }

            foreach (var (nome, valor) in valoresAnteriores)
            {
                Environment.SetEnvironmentVariable(nome, valor);
            }
        }

        base.Dispose(disposing);
    }
}

internal sealed class ProxyRemoteAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var peer = context.Request.Headers["X-Test-Remote-IP"].ToString();
            if (IPAddress.TryParse(peer, out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await nextMiddleware();
        });

        next(app);
    };
}
