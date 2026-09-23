using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class S44SessaoPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Theory]
    [InlineData(PerfisDoPainel.Cliente, null)]
    [InlineData(PerfisDoPainel.Corretor, StatusDoCorretor.EmAnalise)]
    [InlineData(PerfisDoPainel.Supervisor, StatusDoCorretor.Aprovado)]
    public async Task Login_unico_autentica_todos_os_papeis_e_inicia_sessao(string perfil, string? status)
    {
        var (id, email, senha) = await CriarContaAsync(perfil);
        using var client = factory.CreateClient();

        using var resposta = await client.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var sessao = await resposta.Content.ReadFromJsonAsync<SessaoResponse>();
        Assert.NotNull(sessao);
        Assert.Equal(id, sessao.Usuario.Id);
        Assert.Equal(perfil, sessao.Perfil);
        Assert.Equal(status, sessao.StatusCorretor);
        Assert.Contains("Set-Cookie", resposta.Headers.ToString(), StringComparison.OrdinalIgnoreCase);
        var cookie = CookieDe(resposta);

        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var atual = await client.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.OK, atual.StatusCode);
        Assert.Contains(atual.Headers.GetValues("Set-Cookie"), valor =>
            valor.Contains("max-age=2592000", StringComparison.OrdinalIgnoreCase));

        using var sair = await client.DeleteAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.NoContent, sair.StatusCode);
        using var depois = await client.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.Unauthorized, depois.StatusCode);
    }

    [Fact]
    public async Task Erro_de_login_e_generico_e_bloqueio_de_cinco_tentativas_permanece()
    {
        var (_, email, _) = await CriarContaAsync(PerfisDoPainel.Cliente);
        using var client = factory.CreateClient();

        using var inexistente = await client.PostAsJsonAsync("/api/sessoes", new
        {
            email = $"inexistente-{Guid.NewGuid():N}@tests.solar.local",
            senha = "senha-incorreta",
        });
        using var senhaErrada = await client.PostAsJsonAsync("/api/sessoes", new { email, senha = "senha-incorreta" });
        Assert.Equal(HttpStatusCode.Unauthorized, inexistente.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, senhaErrada.StatusCode);
        var erroInexistente = await inexistente.Content.ReadFromJsonAsync<ErroApiResponse>();
        var erroSenha = await senhaErrada.Content.ReadFromJsonAsync<ErroApiResponse>();
        Assert.Equal("credenciais_invalidas", erroInexistente?.Codigo);
        Assert.Equal(erroInexistente?.Codigo, erroSenha?.Codigo);

        for (var tentativa = 0; tentativa < 3; tentativa++)
        {
            using var falha = await client.PostAsJsonAsync("/api/sessoes", new { email, senha = "senha-incorreta" });
            Assert.Equal(HttpStatusCode.Unauthorized, falha.StatusCode);
        }
        using var bloqueado = await client.PostAsJsonAsync("/api/sessoes", new { email, senha = "senha-incorreta" });
        Assert.Equal((HttpStatusCode)423, bloqueado.StatusCode);
        var detalheBloqueio = await bloqueado.Content.ReadFromJsonAsync<BloqueadoResponse>();
        Assert.Equal("bloqueado", detalheBloqueio?.Codigo);
        Assert.InRange(detalheBloqueio!.SegundosRestantes, 1, 30);
    }

    [Fact]
    public async Task Sessao_expira_no_servidor_e_nao_apenas_no_cookie()
    {
        var (id, email, senha) = await CriarContaAsync(PerfisDoPainel.Cliente);
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", CookieDe(login));

        await using (var escopo = factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
            var sessao = await db.Sessoes.SingleAsync(s => s.CorretorId == id && s.RevogadaEm == null);
            sessao.Renovar(DateTimeOffset.UtcNow.AddSeconds(-1));
            await db.SaveChangesAsync();
        }

        using var resposta = await client.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    private async Task<(Guid Id, string Email, string Senha)> CriarContaAsync(string perfil)
    {
        const string senha = "Senha-segura-44";
        var id = Guid.NewGuid();
        var email = $"s44-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            "Conta S-44", email, email.ToLowerInvariant(), "11999998888", "placeholder",
            perfil, ["sul"], [Especialidades.Moradia], "2026-09-22", DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return (Id: conta.Id, Email: email, Senha: senha);
    }

    private static string CookieDe(HttpResponseMessage resposta) =>
        resposta.Headers.GetValues("Set-Cookie").Single()
            .Split(';', 2)[0];
}
