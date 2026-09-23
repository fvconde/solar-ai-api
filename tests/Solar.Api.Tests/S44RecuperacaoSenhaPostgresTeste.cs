using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class S44RecuperacaoSenhaPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Rotas_unificadas_redefinem_senha_de_todos_os_papeis_e_revogam_sessoes_anteriores()
    {
        var remetente = new RemetenteDeRecuperacao();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuracao) => configuracao.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Painel:UrlBaseDoFront"] = "http://localhost:4200",
                }));
            builder.ConfigureTestServices(servicos =>
            {
                servicos.RemoveAll<IEnviadorEmail>();
                servicos.AddSingleton<IEnviadorEmail>(remetente);
            });
        });

        foreach (var perfil in new[]
        {
            PerfisDoPainel.Cliente,
            PerfisDoPainel.Corretor,
            PerfisDoPainel.Supervisor,
        })
        {
            var (_, email, senha) = await CriarContaAsync(perfil);
            using var sessaoAnterior = await LoginAsync(host, email, senha);
            using var solicitado = await sessaoAnterior.PostAsJsonAsync(
                "/api/senha/recuperacoes", new { email });
            Assert.Equal(HttpStatusCode.Accepted, solicitado.StatusCode);

            var enviado = remetente.Recuperacoes.Single(m => m.Email == email);
            var token = ExtrairToken(enviado.Link);
            using var validado = await sessaoAnterior.GetAsync(
                $"/api/senha/recuperacoes/{Uri.EscapeDataString(token)}");
            Assert.Equal(HttpStatusCode.OK, validado.StatusCode);
            Assert.Equal(email, (await validado.Content.ReadFromJsonAsync<RecuperacaoSenhaTokenResponse>())?.Email);

            using var clienteReset = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            const string novaSenha = "Nova-senha-S44";
            using var redefinido = await clienteReset.PostAsJsonAsync(
                "/api/senha", new { token, novaSenha });
            Assert.Equal(HttpStatusCode.OK, redefinido.StatusCode);
            var respostaSessao = await redefinido.Content.ReadFromJsonAsync<SessaoResponse>();
            Assert.NotNull(respostaSessao);
            Assert.Equal(perfil, respostaSessao.Perfil);
            Assert.Equal(email, respostaSessao.Usuario.Email);
            Assert.Contains("Set-Cookie", redefinido.Headers.ToString(), StringComparison.OrdinalIgnoreCase);

            using var sessaoRevogada = await sessaoAnterior.GetAsync("/api/sessao");
            Assert.Equal(HttpStatusCode.Unauthorized, sessaoRevogada.StatusCode);

            var cookie = redefinido.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
            clienteReset.DefaultRequestHeaders.Add("Cookie", cookie);
            using var novaSessao = await clienteReset.GetAsync("/api/sessao");
            Assert.Equal(HttpStatusCode.OK, novaSessao.StatusCode);
            Assert.Equal(perfil, (await novaSessao.Content.ReadFromJsonAsync<SessaoResponse>())?.Perfil);

            using var tokenConsumido = await factory.CreateClient().PostAsJsonAsync(
                "/api/senha", new { token, novaSenha = "Outra-senha-S44" });
            Assert.Equal(HttpStatusCode.Gone, tokenConsumido.StatusCode);

            using var novaSenhaLogin = await LoginAsync(host, email, novaSenha);
            using var senhaAntigaLogin = await factory.CreateClient().PostAsJsonAsync(
                "/api/sessoes", new { email, senha });
            Assert.Equal(HttpStatusCode.Unauthorized, senhaAntigaLogin.StatusCode);
        }
    }

    private async Task<(Guid Id, string Email, string Senha)> CriarContaAsync(string perfil)
    {
        const string senha = "Senha-segura-44";
        var email = $"reset-{perfil}-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            $"Conta {perfil}", email, email, "11999998888", "placeholder", perfil,
            ["centro"], [Especialidades.Moradia], AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return (conta.Id, email, senha);
    }

    private static async Task<HttpClient> LoginAsync(
        WebApplicationFactory<Program> host,
        string email,
        string senha)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var resposta = await client.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
        return client;
    }

    private static string ExtrairToken(string link)
    {
        var query = new Uri(link).Query.TrimStart('?');
        var parametro = query.Split('&').Single(par => par.StartsWith("token=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(parametro["token=".Length..]);
    }

    private sealed record RecuperacaoEnviada(string Email, string Link);

    private sealed class RemetenteDeRecuperacao : IEnviadorEmail
    {
        public List<RecuperacaoEnviada> Recuperacoes { get; } = [];

        public Task EnviarLinkRecuperacaoAsync(
            string destinatario,
            string link,
            CancellationToken cancellationToken = default)
        {
            Recuperacoes.Add(new RecuperacaoEnviada(destinatario, link));
            return Task.CompletedTask;
        }
    }
}
