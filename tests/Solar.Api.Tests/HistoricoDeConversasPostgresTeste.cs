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
public sealed class HistoricoDeConversasPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Historico_de_cliente_ordena_por_atualizacao_e_monta_titulo_e_estado_sem_llm()
    {
        var (contaId, email, senha) = await CriarContaAsync(PerfisDoPainel.Cliente, "historico-cliente");
        using var client = await CriarSessaoAsync(email, senha);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            using var resposta = await client.PostAsJsonAsync(
                $"/conversas/{id:D}/consentimento", new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        }

        var agora = DateTimeOffset.UtcNow;
        await using (var escopo = factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
            var conversas = await db.Conversas.Include(c => c.Lead)
                .Where(c => ids.Contains(c.Id) && c.ContaId == contaId)
                .ToDictionaryAsync(c => c.Id);
            conversas[ids[0]].Lead.Fundir(Intencoes.Compra,
                new CamposExtraidos(Quartos: 2, Regiao: "Zona Sul"), agora);
            conversas[ids[1]].DefinirDesfecho("encerrar");
            db.Encaminhamentos.Add(Encaminhamento.Novo(
                ids[2], conversas[ids[2]].LeadId, null, Especialidades.Moradia, agora));
            DefinirAtualizadaEm(conversas[ids[0]], agora.AddMinutes(-3));
            DefinirAtualizadaEm(conversas[ids[1]], agora.AddMinutes(-1));
            DefinirAtualizadaEm(conversas[ids[2]], agora);
            DefinirAtualizadaEm(conversas[ids[3]], agora.AddMinutes(-2));
            await db.SaveChangesAsync();
        }

        using var respostaHistorico = await client.GetAsync("/api/conta/conversas");
        Assert.Equal(HttpStatusCode.OK, respostaHistorico.StatusCode);
        var historico = await respostaHistorico.Content.ReadFromJsonAsync<IReadOnlyList<ConversaResumo>>();
        Assert.NotNull(historico);
        Assert.Equal(ids[2], historico[0].Id);
        Assert.Equal("com_corretor", historico[0].Estado);
        Assert.Equal(ids[1], historico[1].Id);
        Assert.Equal("encerrada", historico[1].Estado);
        Assert.Equal(ids[3], historico[2].Id);
        Assert.StartsWith("Conversa de ", historico[2].Titulo);
        Assert.Equal(ids[0], historico[3].Id);
        Assert.Equal("2 quartos na zona sul", historico[3].Titulo);
        Assert.All(historico, conversa => Assert.False(string.IsNullOrWhiteSpace(conversa.Titulo)));
    }

    [Theory]
    [InlineData(PerfisDoPainel.Corretor)]
    [InlineData(PerfisDoPainel.Supervisor)]
    public async Task Historico_retorna_403_para_perfis_de_painel(string perfil)
    {
        var (_, email, senha) = await CriarContaAsync(perfil, "historico-painel");
        using var client = await CriarSessaoAsync(email, senha);
        using var resposta = await client.GetAsync("/api/conta/conversas");
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
    }

    private async Task<(Guid Id, string Email, string Senha)> CriarContaAsync(string perfil, string tag)
    {
        const string senha = "Senha-segura-44";
        var email = $"{tag}-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            $"Conta {tag}", email, email, "11999998888", "placeholder", perfil,
            ["centro"], [Especialidades.Moradia], AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
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

    private static void DefinirAtualizadaEm(Conversa conversa, DateTimeOffset em) =>
        typeof(Conversa).GetProperty(nameof(Conversa.AtualizadaEm))!.SetValue(conversa, em);
}
