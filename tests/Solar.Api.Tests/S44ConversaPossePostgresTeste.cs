using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class S44ConversaPossePostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Conversa_vincula_por_login_cadastro_e_sessao_nova_com_isolamento_de_lead_e_404()
    {
        var conversaDona = Guid.NewGuid();
        var conversaSemDona = Guid.NewGuid();
        var conversaCadastro = Guid.NewGuid();
        var conversaNova = Guid.NewGuid();
        var telefoneCompartilhado = $"119{Convert.ToUInt32(Guid.NewGuid().ToString("N")[..8], 16) % 100_000_000:D8}";

        using var anonimo = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await ConsentirAsync(anonimo, conversaDona);
        await ConsentirAsync(anonimo, conversaSemDona);
        await ConsentirAsync(anonimo, conversaCadastro);
        using var contatoDona = await anonimo.PostAsJsonAsync($"/conversas/{conversaDona:D}/contato", new
        {
            nome = "Contato compartilhado",
            telefone = telefoneCompartilhado,
            email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, contatoDona.StatusCode);
        using var contatoSemDona = await anonimo.PostAsJsonAsync($"/conversas/{conversaSemDona:D}/contato", new
        {
            nome = "Contato compartilhado",
            telefone = telefoneCompartilhado,
            email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, contatoSemDona.StatusCode);

        var (donaId, emailDona, senhaDona) = await CriarContaAsync("dona");
        using var dona = await LoginAsync(emailDona, senhaDona, conversaDona);
        Assert.Equal(donaId, await ContaDaConversaAsync(conversaDona));
        var idLeadOriginal = await LeadDaConversaAsync(conversaSemDona);
        var idLeadDono = await LeadDaConversaAsync(conversaDona);
        Assert.NotEqual(idLeadOriginal, idLeadDono);
        Assert.Equal(idLeadOriginal, await LeadDaConversaAsync(conversaSemDona));

        using var leituraDona = await dona.GetAsync($"/conversas/{conversaDona:D}");
        Assert.Equal(HttpStatusCode.OK, leituraDona.StatusCode);
        using var anônimoSemAcesso = await anonimo.GetAsync($"/conversas/{conversaDona:D}");
        Assert.Equal(HttpStatusCode.NotFound, anônimoSemAcesso.StatusCode);
        using var consentimentoDeOutro = await anonimo.PostAsJsonAsync(
            $"/conversas/{conversaDona:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.NotFound, consentimentoDeOutro.StatusCode);
        using var contatoDeOutro = await anonimo.PostAsJsonAsync(
            $"/conversas/{conversaDona:D}/contato", new { nome = "Outra pessoa", telefone = "11999998888", email = (string?)null });
        Assert.Equal(HttpStatusCode.NotFound, contatoDeOutro.StatusCode);
        using var mensagemDeOutro = await anonimo.PostAsJsonAsync(
            $"/conversas/{conversaDona:D}/mensagens", new { texto = "Mensagem de outra sessão" });
        Assert.Equal(HttpStatusCode.NotFound, mensagemDeOutro.StatusCode);

        var emailCadastro = $"cadastro-{Guid.NewGuid():N}@tests.solar.local";
        using var cadastro = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var sessaoCadastro = await cadastro.PostAsJsonAsync("/api/contas", new
        {
            nome = "Cliente cadastro",
            email = emailCadastro,
            telefone = "11911112222",
            senha = "Senha-segura-44",
            aceitePrivacidade = true,
            conversaId = conversaCadastro,
        });
        Assert.Equal(HttpStatusCode.Created, sessaoCadastro.StatusCode);
        var cadastroId = (await sessaoCadastro.Content.ReadFromJsonAsync<SessaoResponse>())!.Usuario.Id;
        Assert.Equal(cadastroId, await ContaDaConversaAsync(conversaCadastro));
        cadastro.DefaultRequestHeaders.Add("Cookie", CookieDe(sessaoCadastro));
        using var contatoCadastro = await cadastro.PostAsJsonAsync($"/conversas/{conversaCadastro:D}/contato", new
        {
            nome = "Contato de outra conta",
            telefone = telefoneCompartilhado,
            email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, contatoCadastro.StatusCode);
        var idLeadCadastro = await LeadDaConversaAsync(conversaCadastro);
        Assert.NotEqual(idLeadDono, idLeadCadastro);

        using var novaConsentida = await dona.PostAsJsonAsync(
            $"/conversas/{conversaNova:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, novaConsentida.StatusCode);
        Assert.Equal(donaId, await ContaDaConversaAsync(conversaNova));

        using var contatoMesmaConta = await dona.PostAsJsonAsync($"/conversas/{conversaNova:D}/contato", new
        {
            nome = "Contato repetido pela mesma conta",
            telefone = telefoneCompartilhado,
            email = (string?)null,
        });
        Assert.Equal(HttpStatusCode.OK, contatoMesmaConta.StatusCode);
        Assert.Equal(idLeadDono, await LeadDaConversaAsync(conversaNova));

        using var outraSessao = await LoginAsync(emailCadastro, "Senha-segura-44", conversaSemDona);
        Assert.Equal(cadastroId, await ContaDaConversaAsync(conversaSemDona));
        using var leituraDeOutraConta = await outraSessao.GetAsync($"/conversas/{conversaDona:D}");
        Assert.Equal(HttpStatusCode.NotFound, leituraDeOutraConta.StatusCode);
        using var leituraOutraDona = await outraSessao.GetAsync($"/conversas/{conversaSemDona:D}");
        Assert.Equal(HttpStatusCode.OK, leituraOutraDona.StatusCode);
        Assert.NotNull(await ConsentimentoDaConversaAsync(conversaDona));
    }

    private async Task ConsentirAsync(HttpClient client, Guid id)
    {
        using var resposta = await client.PostAsJsonAsync(
            $"/conversas/{id:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    private async Task<(Guid Id, string Email, string Senha)> CriarContaAsync(string tag)
    {
        const string senha = "Senha-segura-44";
        var email = $"{tag}-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            $"Cliente {tag}", email, email, "11999998888", "placeholder", PerfisDoPainel.Cliente,
            [], [], "2026-09-22", DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return (conta.Id, email, senha);
    }

    private async Task<HttpClient> LoginAsync(string email, string senha, Guid conversaId)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var resposta = await client.PostAsJsonAsync("/api/sessoes", new { email, senha, conversaId });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", CookieDe(resposta));
        return client;
    }

    private async Task<Guid?> ContaDaConversaAsync(Guid id)
    {
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        return await db.Conversas.Where(c => c.Id == id).Select(c => c.ContaId).SingleAsync();
    }

    private async Task<Guid> LeadDaConversaAsync(Guid id)
    {
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        return await db.Conversas.Where(c => c.Id == id).Select(c => c.LeadId).SingleAsync();
    }

    private async Task<DateTimeOffset?> ConsentimentoDaConversaAsync(Guid id)
    {
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        return await db.Conversas.Where(c => c.Id == id).Select(c => c.Lead.ConsentimentoEm).SingleAsync();
    }

    private static string CookieDe(HttpResponseMessage resposta) =>
        resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0];
}
