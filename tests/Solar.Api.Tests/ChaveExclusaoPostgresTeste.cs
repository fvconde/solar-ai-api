using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class ChaveExclusaoPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Theory]
    [InlineData("Development", false)]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    public async Task Nascimento_anonimo_emite_cookie_restrito_e_persiste_somente_hash(
        string ambiente, bool secure)
    {
        using var aplicacao = factory.WithWebHostBuilder(builder => builder.UseEnvironment(ambiente));
        using var cliente = CriarCliente(aplicacao);
        var id = Guid.NewGuid();

        using var resposta = await ConsentirAsync(cliente, id);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var cookie = CookieDaChave(resposta);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal(secure, cookie.Secure);
        Assert.Equal($"/conversas/{id:D}", cookie.Path.ToString());
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);
        Assert.Null(cookie.MaxAge);
        var chave = cookie.Value.ToString();
        Assert.Equal(TokenSeguro.TamanhoEmBytes, Decodificar(chave).Length);

        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conversa = await db.Conversas.Include(c => c.Lead).SingleAsync(c => c.Id == id);
        Assert.Null(conversa.ContaId);
        Assert.NotNull(conversa.Lead.ConsentimentoEm);
        Assert.NotNull(conversa.ChaveExclusaoHash);
        Assert.Equal(32, conversa.ChaveExclusaoHash.Length);
        Assert.True(TokenSeguro.HashesIguais(conversa.ChaveExclusaoHash, TokenSeguro.Sha256(chave)));
        Assert.False(conversa.ChaveExclusaoHash.SequenceEqual(Decodificar(chave)));
        Assert.False(await db.Mensagens.AnyAsync(m => m.ConversaId == id));

        var corpo = await resposta.Content.ReadAsStringAsync();
        Assert.False(corpo.Contains(chave, StringComparison.Ordinal));
        using var json = JsonDocument.Parse(corpo);
        Assert.Equal(
            new[] { "conversaId", "leadId", "consentimentoEm", "versaoAvisoPrivacidade" },
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        using var leitura = await cliente.GetAsync($"/conversas/{id:D}");
        Assert.Equal(HttpStatusCode.OK, leitura.StatusCode);
        var historico = await leitura.Content.ReadAsStringAsync();
        Assert.False(historico.Contains(chave, StringComparison.Ordinal));
        Assert.False(historico.Contains("chaveExclusao", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hash_sobrevive_a_novo_contexto_e_reinicio_da_aplicacao_sem_reemissao()
    {
        var id = Guid.NewGuid();
        string chave;
        using (var primeiraAplicacao = new PainelApiFactory())
        using (var cliente = CriarCliente(primeiraAplicacao))
        using (var resposta = await ConsentirAsync(cliente, id))
        {
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            chave = CookieDaChave(resposta).Value.ToString();
        }

        await using (var novoContexto = await PostgresTestDatabase.CriarContextoAsync())
        {
            var hash = await novoContexto.Conversas.Where(c => c.Id == id)
                .Select(c => c.ChaveExclusaoHash).SingleAsync();
            Assert.True(TokenSeguro.HashesIguais(hash, TokenSeguro.Sha256(chave)));
        }

        using var segundaAplicacao = new PainelApiFactory();
        using var segundoCliente = CriarCliente(segundaAplicacao);
        segundoCliente.DefaultRequestHeaders.Add("Cookie", $"{ConversasController.NomeCookieChaveExclusao}={chave}");
        using var repeticao = await ConsentirAsync(segundoCliente, id);
        Assert.Equal(HttpStatusCode.OK, repeticao.StatusCode);
        AssertSemCookie(repeticao);
        await using var escopo = segundaAplicacao.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var persistido = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.True(TokenSeguro.HashesIguais(persistido.ChaveExclusaoHash, TokenSeguro.Sha256(chave)));
    }

    [Fact]
    public async Task Conta_autenticada_cria_conversa_com_dono_sem_chave_e_sem_cookie_de_exclusao()
    {
        using var cliente = await ClienteComContaAsync();
        var id = Guid.NewGuid();
        using var resposta = await ConsentirAsync(cliente, id);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        AssertSemCookie(resposta);

        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conversa = await db.Conversas.SingleAsync(c => c.Id == id);
        Assert.NotNull(conversa.ContaId);
        Assert.Null(conversa.ChaveExclusaoHash);

        using var repeticao = await ConsentirAsync(cliente, id);
        Assert.Equal(HttpStatusCode.OK, repeticao.StatusCode);
        AssertSemCookie(repeticao);
        using var anonimo = CriarCliente(factory);
        using var tentativa = await ConsentirAsync(anonimo, id);
        Assert.Equal(HttpStatusCode.NotFound, tentativa.StatusCode);
        AssertSemCookie(tentativa);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Conversas.SingleAsync(c => c.Id == id)).ChaveExclusaoHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversa_legada_existente_nao_recebe_chave_no_consentimento(bool jaConsentida)
    {
        var id = Guid.NewGuid();
        await using (var db = await PostgresTestDatabase.CriarContextoAsync())
        {
            var conversa = Conversa.Nova(id, Canais.Web, DateTimeOffset.UtcNow);
            if (jaConsentida)
                conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        using var cliente = CriarCliente(factory);
        using var resposta = await ConsentirAsync(cliente, id);
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        AssertSemCookie(resposta);
        await using var novoContexto = await PostgresTestDatabase.CriarContextoAsync();
        var persistida = await novoContexto.Conversas.Include(c => c.Lead).SingleAsync(c => c.Id == id);
        Assert.Null(persistida.ChaveExclusaoHash);
        Assert.NotNull(persistida.Lead.ConsentimentoEm);
    }

    [Fact]
    public async Task Corretor_autenticado_cria_conversa_sem_dono_com_chave_e_exclui_por_posse_da_chave()
    {
        using var corretor = await ClienteComContaAsync(PerfisDoPainel.Corretor);
        var id = Guid.NewGuid();
        using var nascimento = await ConsentirAsync(corretor, id);
        Assert.Equal(HttpStatusCode.OK, nascimento.StatusCode);
        var cookie = Assert.Single(nascimento.Headers.GetValues("Set-Cookie")
                .Select(valor => SetCookieHeaderValue.Parse(valor)),
            valor => valor.Name.Equals(ConversasController.NomeCookieChaveExclusao, StringComparison.Ordinal));
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.False(cookie.Secure);
        Assert.Equal($"/conversas/{id:D}", cookie.Path.ToString());
        var chave = cookie.Value.ToString();
        Assert.Equal(TokenSeguro.TamanhoEmBytes, Decodificar(chave).Length);
        Guid leadId;
        await using (var db = await PostgresTestDatabase.CriarContextoAsync())
        {
            var conversa = await db.Conversas.SingleAsync(c => c.Id == id);
            Assert.Null(conversa.ContaId);
            Assert.True(TokenSeguro.HashesIguais(conversa.ChaveExclusaoHash, TokenSeguro.Sha256(chave)));
            leadId = conversa.LeadId;
        }

        using var repeticao = await ConsentirAsync(corretor, id);
        Assert.Equal(HttpStatusCode.OK, repeticao.StatusCode);
        AssertSemCookie(repeticao);
        await using (var db = await PostgresTestDatabase.CriarContextoAsync())
        {
            var conversa = await db.Conversas.SingleAsync(c => c.Id == id);
            Assert.Null(conversa.ContaId);
            Assert.True(TokenSeguro.HashesIguais(conversa.ChaveExclusaoHash, TokenSeguro.Sha256(chave)));
        }
        using var semChave = await corretor.DeleteAsync($"/conversas/{id:D}/titular");
        Assert.Equal(HttpStatusCode.Forbidden, semChave.StatusCode);
        AssertSemCookie(semChave);

        using var possuidora = CriarCliente(factory);
        possuidora.DefaultRequestHeaders.Add("Cookie", $"{ConversasController.NomeCookieChaveExclusao}={chave}");
        using var excluida = await possuidora.DeleteAsync($"/conversas/{id:D}/titular");
        Assert.Equal(HttpStatusCode.OK, excluida.StatusCode);
        var resultado = await excluida.Content.ReadFromJsonAsync<ExclusaoTitularResponse>();
        Assert.NotNull(resultado);
        Assert.True(resultado.LeadExcluido);
        Assert.Equal("lead_e_vinculos", resultado.Escopo);
        await using var verificar = await PostgresTestDatabase.CriarContextoAsync();
        Assert.False(await verificar.Conversas.AnyAsync(c => c.Id == id));
        Assert.False(await verificar.Leads.AnyAsync(l => l.Id == leadId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consentimento_repetido_com_ou_sem_cookie_nao_reemite_nem_rotaciona(bool enviarCookie)
    {
        var id = Guid.NewGuid();
        using var cliente = CriarCliente(factory);
        using var nascimento = await ConsentirAsync(cliente, id);
        Assert.Equal(HttpStatusCode.OK, nascimento.StatusCode);
        var chave = CookieDaChave(nascimento).Value.ToString();
        var respostaOriginal = await nascimento.Content.ReadFromJsonAsync<ConsentimentoResponse>();
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var consentimentoPersistido = await db.Conversas.Where(c => c.Id == id)
            .Select(c => c.Lead.ConsentimentoEm).SingleAsync();

        using var repetidor = CriarCliente(factory);
        if (enviarCookie)
            repetidor.DefaultRequestHeaders.Add("Cookie", $"{ConversasController.NomeCookieChaveExclusao}={chave}");
        using var repeticao = await ConsentirAsync(repetidor, id);
        Assert.Equal(HttpStatusCode.OK, repeticao.StatusCode);
        AssertSemCookie(repeticao);
        var respostaRepetida = await repeticao.Content.ReadFromJsonAsync<ConsentimentoResponse>();
        Assert.NotNull(respostaOriginal);
        Assert.NotNull(respostaRepetida);
        Assert.Equal(respostaOriginal.ConversaId, respostaRepetida.ConversaId);
        Assert.Equal(respostaOriginal.LeadId, respostaRepetida.LeadId);
        Assert.Equal(respostaOriginal.VersaoAvisoPrivacidade, respostaRepetida.VersaoAvisoPrivacidade);
        Assert.Equal(consentimentoPersistido, respostaRepetida.ConsentimentoEm);
        var persistida = await db.Conversas.SingleAsync(c => c.Id == id);
        Assert.True(TokenSeguro.HashesIguais(persistida.ChaveExclusaoHash, TokenSeguro.Sha256(chave)));
    }

    [Fact]
    public async Task Consentimento_invalido_nao_emite_cookie_nem_cria_conversa_ou_lead()
    {
        var id = Guid.NewGuid();
        using var cliente = CriarCliente(factory);
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var quantidadeLeads = await db.Leads.CountAsync();
        using var resposta = await cliente.PostAsJsonAsync(
            $"/conversas/{id:D}/consentimento", new { versaoAvisoPrivacidade = "invalida" });
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        AssertSemCookie(resposta);
        Assert.False(await db.Conversas.AnyAsync(c => c.Id == id));
        Assert.Equal(quantidadeLeads, await db.Leads.CountAsync());
    }

    private async Task<HttpClient> ClienteComContaAsync(string perfil = PerfisDoPainel.Cliente)
    {
        const string senha = "Senha-segura-38";
        var email = $"chave-exclusao-{Guid.NewGuid():N}@tests.solar.local";
        await using (var escopo = factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
            var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
            var conta = Corretor.NovaConta("Cliente T1", email, email, "11999998888", "placeholder",
                perfil, [], [], AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
            conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
            db.Corretores.Add(conta);
            await db.SaveChangesAsync();
        }

        var cliente = CriarCliente(factory);
        using var sessao = await cliente.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, sessao.StatusCode);
        var respostaSessao = await sessao.Content.ReadFromJsonAsync<SessaoResponse>();
        Assert.NotNull(respostaSessao);
        Assert.Equal(perfil, respostaSessao.Perfil);
        cliente.DefaultRequestHeaders.Add("Cookie", sessao.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
        return cliente;
    }

    private static HttpClient CriarCliente(WebApplicationFactory<Program> aplicacao) =>
        aplicacao.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static Task<HttpResponseMessage> ConsentirAsync(HttpClient cliente, Guid id) =>
        cliente.PostAsJsonAsync($"/conversas/{id:D}/consentimento",
            new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });

    private static SetCookieHeaderValue CookieDaChave(HttpResponseMessage resposta)
    {
        Assert.True(resposta.Headers.TryGetValues("Set-Cookie", out var cookies));
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(cookies!));
        Assert.Equal(ConversasController.NomeCookieChaveExclusao, cookie.Name.ToString());
        Assert.True(cookie.Value.HasValue);
        return cookie;
    }

    private static void AssertSemCookie(HttpResponseMessage resposta)
    {
        if (!resposta.Headers.TryGetValues("Set-Cookie", out var cookies))
            return;
        foreach (var valor in cookies)
        {
            var cookie = SetCookieHeaderValue.Parse(valor);
            Assert.False(cookie.Name.Equals(ConversasController.NomeCookieChaveExclusao, StringComparison.Ordinal));
        }
    }

    private static byte[] Decodificar(string chave) =>
        Convert.FromBase64String(chave.Replace('-', '+').Replace('_', '/') + "=");
}
