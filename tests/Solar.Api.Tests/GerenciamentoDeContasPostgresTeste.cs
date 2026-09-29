using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class GerenciamentoDeContasPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Conta_permite_edicao_segura_troca_de_email_e_senha_revogando_outras_sessoes()
    {
        var (id, email, senha) = await CriarContaAsync(PerfisDoPainel.Cliente, "cliente-edicao");
        using var atual = await CriarSessaoAsync(email, senha);
        using var outraSessao = await CriarSessaoAsync(email, senha);

        using var tentativaErrada = await atual.PatchAsJsonAsync("/api/conta", new
        {
            email = $"novo-{Guid.NewGuid():N}@tests.solar.local",
            senhaAtual = "senha-errada",
        });
        Assert.Equal(HttpStatusCode.BadRequest, tentativaErrada.StatusCode);
        Assert.Equal("senha_atual_incorreta", (await tentativaErrada.Content.ReadFromJsonAsync<ErroApiResponse>())?.Codigo);

        var novoEmail = $"atualizado-{Guid.NewGuid():N}@tests.solar.local";
        using var alteracao = await atual.PatchAsJsonAsync("/api/conta", new
        {
            nome = "Nome Atualizado",
            telefone = "(11) 98888-7777",
            email = novoEmail,
            senhaAtual = senha,
        });
        Assert.Equal(HttpStatusCode.OK, alteracao.StatusCode);
        var conta = await alteracao.Content.ReadFromJsonAsync<ContaResponse>();
        Assert.NotNull(conta);
        Assert.Equal(id, conta.Id);
        Assert.Equal("Nome Atualizado", conta.Nome);
        Assert.Equal("11988887777", conta.Telefone);
        Assert.Equal(novoEmail, conta.Email);
        Assert.Equal(PerfisDoPainel.Cliente, conta.Perfil);
        Assert.Null(conta.Corretor);
        Assert.NotNull(conta.Consentimento);

        using var contaAtual = await atual.GetAsync("/api/conta");
        Assert.Equal(HttpStatusCode.OK, contaAtual.StatusCode);
        using var outraRevogada = await outraSessao.GetAsync("/api/conta");
        Assert.Equal(HttpStatusCode.Unauthorized, outraRevogada.StatusCode);
        using var bloqueioCliente = await atual.PatchAsJsonAsync("/api/conta", new { regioes = new[] { "sul" } });
        Assert.Equal(HttpStatusCode.Forbidden, bloqueioCliente.StatusCode);

        using var terceiraSessao = await CriarSessaoAsync(novoEmail, senha);
        using var trocaSenha = await atual.PostAsJsonAsync("/api/conta/senha", new
        {
            senhaAtual = senha,
            novaSenha = "Nova-senha-44",
        });
        Assert.Equal(HttpStatusCode.NoContent, trocaSenha.StatusCode);
        using var atualPermanece = await atual.GetAsync("/api/conta");
        Assert.Equal(HttpStatusCode.OK, atualPermanece.StatusCode);
        using var sessaoRevogada = await terceiraSessao.GetAsync("/api/conta");
        Assert.Equal(HttpStatusCode.Unauthorized, sessaoRevogada.StatusCode);
        using var senhaNova = await CriarSessaoAsync(novoEmail, "Nova-senha-44");
        using var senhaNovaValida = await senhaNova.GetAsync("/api/conta");
        using var senhaAntiga = await atual.PostAsJsonAsync("/api/sessoes", new { email = novoEmail, senha });
        Assert.Equal(HttpStatusCode.OK, senhaNovaValida.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, senhaAntiga.StatusCode);

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        Assert.Equal(novoEmail.ToLowerInvariant(), await db.Corretores.Where(c => c.Id == id)
            .Select(c => c.EmailNormalizado).SingleAsync());
    }

    [Fact]
    public async Task Exclusao_de_cliente_apaga_suas_conversas_e_leads_sem_afetar_conversa_sem_dono()
    {
        var (id, email, senha) = await CriarContaAsync(PerfisDoPainel.Cliente, "cliente-exclusao");
        using var cliente = await CriarSessaoAsync(email, senha);
        var conversaDoCliente = Guid.NewGuid();
        var conversaSemDono = Guid.NewGuid();
        using var conversaCriada = await cliente.PostAsJsonAsync(
            $"/conversas/{conversaDoCliente:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, conversaCriada.StatusCode);
        using var anonimo = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var conversaPublica = await anonimo.PostAsJsonAsync(
            $"/conversas/{conversaSemDono:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, conversaPublica.StatusCode);

        var leadCliente = await LeadDaConversaAsync(conversaDoCliente);
        var leadSemDono = await LeadDaConversaAsync(conversaSemDono);
        using var exclusao = await cliente.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/conta")
        {
            Content = JsonContent.Create(new { email }),
        });
        Assert.Equal(HttpStatusCode.NoContent, exclusao.StatusCode);
        using var sessaoDepois = await cliente.GetAsync("/api/conta");
        Assert.Equal(HttpStatusCode.Unauthorized, sessaoDepois.StatusCode);

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        Assert.False(await db.Corretores.AnyAsync(c => c.Id == id));
        Assert.False(await db.Conversas.AnyAsync(c => c.Id == conversaDoCliente));
        Assert.False(await db.Leads.AnyAsync(l => l.Id == leadCliente));
        Assert.True(await db.Conversas.AnyAsync(c => c.Id == conversaSemDono && c.ContaId == null));
        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadSemDono));
    }

    [Fact]
    public async Task Exclusao_de_corretor_redistribui_encaminhamentos_pela_especialidade_e_regiao()
    {
        var (excluirId, email, senha) = await CriarContaAsync(PerfisDoPainel.Corretor, "corretor-exclusao", aprovado: true);
        var regiaoExclusiva = $"s44 exclusivo {Guid.NewGuid():N}";
        var (destinoId, _, _) = await CriarContaAsync(
            PerfisDoPainel.Corretor, "corretor-destino", aprovado: true, regiao: regiaoExclusiva);
        var agora = DateTimeOffset.UtcNow;
        Guid conversaId;
        long encaminhamentoId;
        await using (var escopo = factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
            var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, agora);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: regiaoExclusiva), agora);
            db.Conversas.Add(conversa);
            var encaminhamento = Encaminhamento.Novo(
                conversa.Id, conversa.LeadId, excluirId, Especialidades.Moradia, agora);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
            conversaId = conversa.Id;
            encaminhamentoId = encaminhamento.Id;
        }

        using var client = await CriarSessaoAsync(email, senha);
        using var resposta = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/conta")
        {
            Content = JsonContent.Create(new { email }),
        });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        await using var verificar = factory.Services.CreateAsyncScope();
        var contexto = verificar.ServiceProvider.GetRequiredService<SolarDbContext>();
        var atualizado = await contexto.Encaminhamentos.SingleAsync(e => e.Id == encaminhamentoId);
        Assert.Equal(destinoId, atualizado.CorretorId);
        Assert.Equal(StatusDoEncaminhamento.Atribuido, atualizado.Status);
        Assert.False(await contexto.Corretores.AnyAsync(c => c.Id == excluirId));
        Assert.True(await contexto.Corretores.AnyAsync(c => c.Id == destinoId && c.StatusCorretor == StatusDoCorretor.Aprovado));
        Assert.True(await contexto.Conversas.AnyAsync(c => c.Id == conversaId));
    }

    [Fact]
    public async Task Supervisor_nao_pode_excluir_a_propria_conta()
    {
        var (id, email, senha) = await CriarContaAsync(PerfisDoPainel.Supervisor, "supervisor-nao-excluir", aprovado: true);
        using var supervisor = await CriarSessaoAsync(email, senha);
        using var resposta = await supervisor.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/conta")
        {
            Content = JsonContent.Create(new { email }),
        });
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal("nao_permitido", (await resposta.Content.ReadFromJsonAsync<ErroApiResponse>())?.Codigo);

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        Assert.True(await db.Corretores.AnyAsync(c => c.Id == id));
    }

    private async Task<(Guid Id, string Email, string Senha)> CriarContaAsync(
        string perfil,
        string tag,
        bool aprovado = false,
        string regiao = "centro")
    {
        const string senha = "Senha-segura-44";
        var email = $"{tag}-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            $"Conta {tag}", email, email, "11999998888", "placeholder", perfil,
            [regiao], [Especialidades.Moradia], AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        if (aprovado) conta.Aprovar(DateTimeOffset.UtcNow);
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return (conta.Id, email, senha);
    }

    private async Task<HttpClient> CriarSessaoAsync(string email, string senha)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var resposta = await client.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
        return client;
    }

    private async Task<Guid> LeadDaConversaAsync(Guid conversaId)
    {
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        return await db.Conversas.Where(c => c.Id == conversaId).Select(c => c.LeadId).SingleAsync();
    }
}
