using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class JanelaRespostaFollowUpPostgresTeste(MetricasPainelPostgresFixture fixture)
    : IClassFixture<MetricasPainelPostgresFixture>
{
    private static readonly DateTimeOffset Agora = MetricasPainelPostgresFixture.Agora;

    [Fact]
    public async Task Appsettings_e_factory_trazem_janela_7_e_get_supervisor_retorna_200()
    {
        var config = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var valorInicial = config["Metricas:JanelaRespostaFollowUpDias"];
        try
        {
            var janela = config.GetValue<int>("Metricas:JanelaRespostaFollowUpDias");
            Assert.Equal(7, janela);

            var supervisorToken = await ObterOuCriarSupervisorTokenAsync();
            using var client = Cliente(supervisorToken);
            using var resposta = await client.GetAsync("/api/painel/metricas");
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var metricas = await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>();
            Assert.NotNull(metricas);
            Assert.Equal(7, metricas.Extras.FollowUp.JanelaDias);
        }
        finally
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = valorInicial;
        }
    }

    [Fact]
    public async Task Configuracao_com_valor_3_atualiza_iconfiguration_e_get_retorna_200()
    {
        var config = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var valorInicial = config["Metricas:JanelaRespostaFollowUpDias"];
        try
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = "3";
            var janela = config.GetValue<int>("Metricas:JanelaRespostaFollowUpDias");
            Assert.Equal(3, janela);

            var supervisorToken = await ObterOuCriarSupervisorTokenAsync();
            using var client = Cliente(supervisorToken);
            using var resposta = await client.GetAsync("/api/painel/metricas");
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var metricas = await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>();
            Assert.NotNull(metricas);
            Assert.Equal(3, metricas.Extras.FollowUp.JanelaDias);
        }
        finally
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = valorInicial;
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public async Task Janela_invalida_retorna_500_com_mensagem_para_supervisor_e_pendente(
        int diasInvalidos, bool supervisor)
    {
        var config = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var valorInicial = config["Metricas:JanelaRespostaFollowUpDias"];
        try
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = diasInvalidos.ToString();

            var token = supervisor
                ? await ObterOuCriarSupervisorTokenAsync()
                : await ObterOuCriarPendenteTokenAsync();

            using var client = Cliente(token);
            using var resposta = await client.GetAsync("/api/painel/metricas");
            Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

            var corpo = await resposta.Content.ReadAsStringAsync();
            Assert.Contains("Metricas:JanelaRespostaFollowUpDias deve ser positivo.", corpo);
        }
        finally
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = valorInicial;
        }
    }

    private async Task<string> ObterOuCriarSupervisorTokenAsync()
    {
        return await CriarContaESessaoAsync("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
    }

    private async Task<string> ObterOuCriarPendenteTokenAsync()
    {
        return await CriarContaESessaoAsync("Pendente S45", PerfisDoPainel.Corretor, aprovado: false);
    }

    private async Task<string> CriarContaESessaoAsync(string nome, string perfil, bool aprovado)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();

        var email = $"{Guid.NewGuid():N}@tests.solar.local";
        var conta = Corretor.NovaConta(
            nome,
            email,
            email.ToUpperInvariant(),
            "11987654321",
            "hash-s45",
            perfil,
            [],
            [],
            "s45",
            Agora.AddDays(-1));

        if (aprovado && perfil != PerfisDoPainel.Cliente)
        {
            conta.Aprovar(Agora);
        }

        db.Corretores.Add(conta);

        var token = TokenSeguro.Criar();
        var sessao = SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), Agora);
        db.Sessoes.Add(sessao);

        await db.SaveChangesAsync();
        return token;
    }

    private HttpClient Cliente(string? token)
    {
        var client = fixture.Factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={token}");
        }

        return client;
    }
}
