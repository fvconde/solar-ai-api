using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class CadastroDeContasPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Cadastro_cliente_grava_consentimento_e_cria_sessao()
    {
        var email = $"cliente-{Guid.NewGuid():N}@tests.solar.local";
        using var client = factory.CreateClient();
        using var resposta = await client.PostAsJsonAsync("/api/contas", new
        {
            nome = "Cliente de Teste",
            email,
            telefone = "(11) 99999-8888",
            senha = "Senha-segura-44",
            aceitePrivacidade = true,
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var sessao = await resposta.Content.ReadFromJsonAsync<SessaoResponse>();
        Assert.NotNull(sessao);
        Assert.Equal(PerfisDoPainel.Cliente, sessao.Perfil);
        Assert.Null(sessao.StatusCorretor);
        Assert.Contains("Set-Cookie", resposta.Headers.ToString(), StringComparison.OrdinalIgnoreCase);

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var conta = await db.Corretores.SingleAsync(c => c.Id == sessao.Usuario.Id);
        Assert.Equal("11999998888", conta.Telefone);
        Assert.NotNull(conta.ConsentimentoEm);
        Assert.Equal(AvisoPrivacidade.VersaoAtual, conta.VersaoAvisoPrivacidade);
        Assert.NotEqual("Senha-segura-44", conta.SenhaHash);
        Assert.Single(await db.Sessoes.Where(s => s.CorretorId == conta.Id).ToListAsync());

        client.DefaultRequestHeaders.Add("Cookie", CookieDe(resposta));
        using var contaAtual = await client.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.OK, contaAtual.StatusCode);
    }

    [Fact]
    public async Task Cadastro_corretor_fica_em_analise_com_multiplas_especialidades_e_email_globalmente_unico()
    {
        var email = $"corretor-{Guid.NewGuid():N}@tests.solar.local";
        using var client = factory.CreateClient();
        using var resposta = await client.PostAsJsonAsync("/api/corretores", new
        {
            nome = "Corretor de Teste",
            email,
            telefone = "11999998888",
            senha = "Senha-segura-44",
            aceitePrivacidade = true,
            regioes = new[] { "Zona Sul", "Centro" },
            especialidades = new[] { "moradia", "investimento" },
        });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var sessao = await resposta.Content.ReadFromJsonAsync<SessaoResponse>();
        Assert.NotNull(sessao);
        Assert.Equal(PerfisDoPainel.Corretor, sessao.Perfil);
        Assert.Equal(StatusDoCorretor.EmAnalise, sessao.StatusCorretor);

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var conta = await db.Corretores.SingleAsync(c => c.Id == sessao.Usuario.Id);
        Assert.Equal(new[] { "moradia", "investimento" }, conta.Especialidades);
        Assert.Equal(new[] { "zona sul", "centro" }, conta.Regioes);
        Assert.Null(conta.AprovadoEm);

        using var conflito = await client.PostAsJsonAsync("/api/contas", new
        {
            nome = "Cliente Duplicado",
            email = email.ToUpperInvariant(),
            telefone = "11999990000",
            senha = "Senha-segura-44",
            aceitePrivacidade = true,
        });
        Assert.Equal(HttpStatusCode.Conflict, conflito.StatusCode);
        var erro = await conflito.Content.ReadFromJsonAsync<ErroApiResponse>();
        Assert.Equal("email_em_uso", erro?.Codigo);
    }

    [Fact]
    public async Task Cadastro_sem_aceite_de_privacidade_retorna_validacao_de_campo()
    {
        using var client = factory.CreateClient();
        using var resposta = await client.PostAsJsonAsync("/api/contas", new
        {
            nome = "Cliente sem aceite",
            email = $"sem-aceite-{Guid.NewGuid():N}@tests.solar.local",
            telefone = "11999998888",
            senha = "Senha-segura-44",
            aceitePrivacidade = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erro = await resposta.Content.ReadFromJsonAsync<ValidacaoResponse>();
        Assert.Equal("validacao", erro?.Codigo);
        Assert.Equal("valor_invalido", erro?.Campos["aceitePrivacidade"]);
    }

    private static string CookieDe(HttpResponseMessage resposta) =>
        resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
}
