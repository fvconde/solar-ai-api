using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    private async Task<HttpResponseMessage> SolicitarAsync(string peerImediato, string xForwardedFor)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, "/_test/proxy-ip");
        requisicao.Headers.TryAddWithoutValidation("X-Test-Remote-IP", peerImediato);
        requisicao.Headers.TryAddWithoutValidation("X-Forwarded-For", xForwardedFor);
        return await cliente.SendAsync(requisicao);
    }
}

[ApiController]
[Route("_test/proxy-ip")]
public sealed class ProxyAddressProbeController : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting("mensagens")]
    public IActionResult Get() => Content(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
}

public sealed class ProxyTrustApiFactory : WebApplicationFactory<Program>
{
    private static readonly string[] VariaveisDeTeste =
    [
        "ConnectionStrings__Postgres",
        "ProxyTrust__ForwardLimit",
        "ProxyTrust__KnownProxies__0",
        "ProxyTrust__KnownIPNetworks__0",
        "RateLimiting__MensagensPorMinuto",
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
    ];

    private readonly Dictionary<string, string?> valoresAnteriores = VariaveisDeTeste
        .ToDictionary(nome => nome, Environment.GetEnvironmentVariable);

    public ProxyTrustApiFactory()
    {
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres",
            PostgresTestDatabase.ObterConexaoParaAplicacao());
        Environment.SetEnvironmentVariable("ProxyTrust__ForwardLimit", "2");
        Environment.SetEnvironmentVariable("ProxyTrust__KnownProxies__0", "192.0.2.10");
        Environment.SetEnvironmentVariable("ProxyTrust__KnownIPNetworks__0", "198.51.100.0/24");
        Environment.SetEnvironmentVariable("RateLimiting__MensagensPorMinuto", "1");
        Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ProxyAddressProbeController).Assembly);
            services.AddTransient<IStartupFilter, ProxyRemoteAddressStartupFilter>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
