using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class TempoFollowUpPostgresTeste(MetricasPainelPostgresFixture fixture)
    : IClassFixture<MetricasPainelPostgresFixture>
{
    private static readonly DateTimeOffset Agora = MetricasPainelPostgresFixture.Agora;

    private async Task LimparSchemaAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        await db.Slots.ExecuteDeleteAsync();
        await db.Mensagens.ExecuteDeleteAsync();
        await db.Encaminhamentos.ExecuteDeleteAsync();
        await db.Conversas.ExecuteDeleteAsync();
        await db.Leads.ExecuteDeleteAsync();
        await db.Sessoes.ExecuteDeleteAsync();
        await db.Corretores.ExecuteDeleteAsync();
        await db.RegistroMetricas.Where(r => r.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, MetricasPainelPostgresFixture.HistoricoDesde));
    }

    private static Corretor NovaConta(string nome, string perfil, bool aprovado, IReadOnlyList<string>? regioes = null)
    {
        var email = $"{Guid.NewGuid():N}@tests.solar.local";
        var conta = Corretor.NovaConta(
            nome,
            email,
            email.ToUpperInvariant(),
            "11987654321",
            "hash-s45",
            perfil,
            regioes ?? [],
            [Especialidades.Moradia],
            "s45",
            Agora.AddDays(-1));

        if (aprovado && perfil != PerfisDoPainel.Cliente)
        {
            conta.Aprovar(Agora);
        }

        return conta;
    }

    private static async Task<string> CriarSessaoAsync(IServiceProvider services, Corretor conta)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var token = TokenSeguro.Criar();
        db.Sessoes.Add(SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), Agora));
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

    private async Task<MetricasPainelResponse> ObterMetricasAsync(string token, int dias = 30)
    {
        using var client = Cliente(token);
        using var resposta = await client.GetAsync($"/api/painel/metricas?dias={dias}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>())!;
    }

    [Fact]
    public async Task Mediana_impar_e_par_calcula_corretamente_sem_interferencia_de_saudacao_ou_duplo_encaminhamento()
    {
        await LimparSchemaAsync();

        var t0 = Agora.AddDays(-10);
        Corretor supervisor;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            var corretor = NovaConta("Corretor S45", PerfisDoPainel.Corretor, aprovado: true);
            db.Corretores.AddRange(supervisor, corretor);

            var c1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c1, t0.AddMinutes(3));
            db.Conversas.Add(c1);
            db.Mensagens.Add(Mensagem.DoLead(c1.Id, "Mensagem lead 1", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c1.Id, c1.LeadId, corretor.Id, Especialidades.Moradia, t0.AddMinutes(3)));

            var c2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c2, t0.AddMinutes(9));
            db.Conversas.Add(c2);
            db.Mensagens.Add(Mensagem.DaLia(c2.Id, "Saudacao anterior", ProximasAcoes.ContinuarConversa, t0.AddHours(-2), []));
            db.Mensagens.Add(Mensagem.DoLead(c2.Id, "Mensagem lead 2", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c2.Id, c2.LeadId, corretor.Id, Especialidades.Moradia, t0.AddMinutes(9)));

            var c3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c3, t0.AddMinutes(21));
            db.Conversas.Add(c3);
            db.Mensagens.Add(Mensagem.DoLead(c3.Id, "Mensagem lead 3", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c3.Id, c3.LeadId, corretor.Id, Especialidades.Moradia, t0.AddMinutes(21)));
            c3.RegistrarTurno("Quero agendar de novo", new TurnoResponse("Ok", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.AgendarReuniao, [], null, false), t0.AddMinutes(50));
            Assert.Equal(t0.AddMinutes(21), c3.EncaminhadaEm);

            await db.SaveChangesAsync();
        }

        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas3 = await ObterMetricasAsync(token);
        Assert.Equal(9.0, metricas3.Extras.TempoMedianoMin);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var corretor = await db.Corretores.FirstAsync(c => c.Perfil == PerfisDoPainel.Corretor);
            var c4 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c4, t0.AddMinutes(7));
            db.Conversas.Add(c4);
            db.Mensagens.Add(Mensagem.DoLead(c4.Id, "Mensagem lead 4", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c4.Id, c4.LeadId, corretor.Id, Especialidades.Moradia, t0.AddMinutes(7)));
            await db.SaveChangesAsync();
        }

        var metricas4 = await ObterMetricasAsync(token);
        Assert.Equal(8.0, metricas4.Extras.TempoMedianoMin);
    }

    [Fact]
    public async Task Serie_diaria_retorna_ultimos_sete_dias_utc_com_null_em_lacunas_e_array_vazio_quando_sem_dados_recentes()
    {
        await LimparSchemaAsync();

        var hojeUtc = Agora.UtcDateTime.Date;
        Corretor supervisor;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            var corretor = NovaConta("Corretor S45", PerfisDoPainel.Corretor, aprovado: true);
            db.Corretores.AddRange(supervisor, corretor);

            var tOntem = new DateTimeOffset(hojeUtc.AddDays(-1), TimeSpan.Zero);
            var cOntem1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tOntem);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cOntem1, tOntem.AddMinutes(5));
            db.Conversas.Add(cOntem1);
            db.Mensagens.Add(Mensagem.DoLead(cOntem1.Id, "Lead Ontem 1", tOntem));

            var tOntemNoite = new DateTimeOffset(hojeUtc.AddDays(-1), TimeSpan.Zero).AddHours(23).AddMinutes(59);
            var cOntem2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tOntemNoite.AddMinutes(-7));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cOntem2, tOntemNoite);
            db.Conversas.Add(cOntem2);
            db.Mensagens.Add(Mensagem.DoLead(cOntem2.Id, "Lead Ontem 2", tOntemNoite.AddMinutes(-7)));

            var tHojeMadrugada = new DateTimeOffset(hojeUtc, TimeSpan.Zero).AddMinutes(1);
            var cHoje3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tHojeMadrugada.AddMinutes(-9));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cHoje3, tHojeMadrugada);
            db.Conversas.Add(cHoje3);
            db.Mensagens.Add(Mensagem.DoLead(cHoje3.Id, "Lead Hoje 3", tHojeMadrugada.AddMinutes(-9)));

            var tHoje1 = new DateTimeOffset(hojeUtc, TimeSpan.Zero).AddHours(10);
            var cHoje1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tHoje1);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cHoje1, tHoje1.AddMinutes(10));
            db.Conversas.Add(cHoje1);
            db.Mensagens.Add(Mensagem.DoLead(cHoje1.Id, "Lead Hoje 1", tHoje1));

            var tHoje2 = new DateTimeOffset(hojeUtc, TimeSpan.Zero).AddHours(11);
            var cHoje2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tHoje2);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cHoje2, tHoje2.AddMinutes(20));
            db.Conversas.Add(cHoje2);
            db.Mensagens.Add(Mensagem.DoLead(cHoje2.Id, "Lead Hoje 2", tHoje2));

            await db.SaveChangesAsync();
        }

        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(token);

        Assert.Equal(7, metricas.Extras.TempoMedianoDiario.Count);
        Assert.Null(metricas.Extras.TempoMedianoDiario[0]);
        Assert.Null(metricas.Extras.TempoMedianoDiario[1]);
        Assert.Null(metricas.Extras.TempoMedianoDiario[2]);
        Assert.Null(metricas.Extras.TempoMedianoDiario[3]);
        Assert.Null(metricas.Extras.TempoMedianoDiario[4]);
        Assert.Equal(6.0, metricas.Extras.TempoMedianoDiario[5]);
        Assert.Equal(10.0, metricas.Extras.TempoMedianoDiario[6]);

        await LimparSchemaAsync();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            db.Corretores.Add(supervisor);

            var tAntigo = Agora.AddDays(-12);
            var cAntiga = Conversa.Nova(Guid.NewGuid(), Canais.Web, tAntigo);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cAntiga, tAntigo.AddMinutes(10));
            db.Conversas.Add(cAntiga);
            db.Mensagens.Add(Mensagem.DoLead(cAntiga.Id, "Lead antigo", tAntigo));
            await db.SaveChangesAsync();
        }

        var tokenSemRecente = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricasSemRecente = await ObterMetricasAsync(tokenSemRecente);

        Assert.Equal(10.0, metricasSemRecente.Extras.TempoMedianoMin);
        Assert.Empty(metricasSemRecente.Extras.TempoMedianoDiario);
    }

    [Fact]
    public async Task Supervisor_estavel_apos_redistribuicao_preserva_mediana_serie_e_avanco_atualizando_apenas_distribuicao()
    {
        await LimparSchemaAsync();

        var corte = Agora.AddDays(-10);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, corte));
        }

        Corretor supervisor;
        Corretor corretorA;
        Corretor corretorB;

        var tAntesDoCorte = Agora.AddDays(-15);
        var tDepoisDoCorte = Agora.AddDays(-5);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            corretorA = NovaConta("Corretor A", PerfisDoPainel.Corretor, aprovado: true);
            db.Corretores.AddRange(supervisor, corretorA);

            var c1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tAntesDoCorte);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(c1, corte.AddMinutes(10));
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(c1, corte.AddMinutes(20));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c1, corte.AddMinutes(30));
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(c1, corte.AddMinutes(40));
            db.Conversas.Add(c1);
            db.Mensagens.Add(Mensagem.DoLead(c1.Id, "Lead 1", tAntesDoCorte));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c1.Id, c1.LeadId, corretorA.Id, Especialidades.Moradia, corte.AddMinutes(40)));

            var c2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tDepoisDoCorte);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(c2, tDepoisDoCorte.AddMinutes(5));
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(c2, tDepoisDoCorte.AddMinutes(10));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(c2, tDepoisDoCorte.AddMinutes(15));
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(c2, tDepoisDoCorte.AddMinutes(20));
            db.Conversas.Add(c2);
            db.Mensagens.Add(Mensagem.DoLead(c2.Id, "Lead 2", tDepoisDoCorte));
            db.Encaminhamentos.Add(Encaminhamento.Novo(c2.Id, c2.LeadId, corretorA.Id, Especialidades.Moradia, tDepoisDoCorte.AddMinutes(20)));

            await db.SaveChangesAsync();
        }

        try
        {
            var supervisorToken = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
            var metricasAntes = await ObterMetricasAsync(supervisorToken);

            var medianaAntes = metricasAntes.Extras.TempoMedianoMin;
            var serieAntes = metricasAntes.Extras.TempoMedianoDiario;
            var avancoAntes = metricasAntes.Avanco;
            var essenciaisAntes = metricasAntes.DadosEssenciaisPreenchidos;

            Assert.Equal(3622.5, medianaAntes);
            Assert.Equal(1, metricasAntes.Avanco.Single(a => a.Etapa == "iniciadas").Conversas);
            Assert.Equal(1, metricasAntes.Avanco.Single(a => a.Etapa == "essenciais").Conversas);
            Assert.Equal(1, essenciaisAntes);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var cA = await db.Corretores.SingleAsync(c => c.Id == corretorA.Id);
                cA.Desativar();

                corretorB = NovaConta("Corretor B", PerfisDoPainel.Corretor, aprovado: true);
                db.Corretores.Add(corretorB);
                await db.SaveChangesAsync();

                var encRepo = new EncaminhamentoRepositorio(db);
                await encRepo.RedistribuirAsync(corretorA.Id, Agora.AddHours(-1), default);
            }

            var metricasDepois = await ObterMetricasAsync(supervisorToken);
            Assert.Equal(medianaAntes, metricasDepois.Extras.TempoMedianoMin);
            Assert.Equal(serieAntes, metricasDepois.Extras.TempoMedianoDiario);
            Assert.Equal(avancoAntes, metricasDepois.Avanco);
            Assert.Equal(essenciaisAntes, metricasDepois.DadosEssenciaisPreenchidos);

            var itemB = Assert.Single(metricasDepois.Equipe!.AtribuidasPorCorretor);
            Assert.Equal(corretorB.Id, itemB.Corretor.Id);
            Assert.Equal(2, itemB.Conversas);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var cB = await db.Corretores.SingleAsync(c => c.Id == corretorB.Id);
                cB.Desativar();
                await db.SaveChangesAsync();

                var encRepo = new EncaminhamentoRepositorio(db);
                await encRepo.RedistribuirAsync(corretorB.Id, Agora, default);
            }

            var metricasDesatribuida = await ObterMetricasAsync(supervisorToken);
            Assert.Equal(medianaAntes, metricasDesatribuida.Extras.TempoMedianoMin);
            Assert.Equal(serieAntes, metricasDesatribuida.Extras.TempoMedianoDiario);
            Assert.Equal(avancoAntes, metricasDesatribuida.Avanco);
            Assert.Equal(essenciaisAntes, metricasDesatribuida.DadosEssenciaisPreenchidos);
            Assert.Empty(metricasDesatribuida.Equipe!.AtribuidasPorCorretor);
            Assert.Equal(2, metricasDesatribuida.Equipe.AguardandoCorretor);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                await db.RegistroMetricas.Where(r => r.Id == 1)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, MetricasPainelPostgresFixture.HistoricoDesde));
            }
        }
    }

    [Fact]
    public async Task Dados_invalidos_ou_fora_do_recorte_sao_ignorados_na_mediana()
    {
        await LimparSchemaAsync();

        Corretor supervisor;
        Corretor corretor1;
        Corretor corretor2;
        Corretor pendente;

        var t0 = Agora.AddDays(-5);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            corretor1 = NovaConta("Corretor 1", PerfisDoPainel.Corretor, aprovado: true);
            corretor2 = NovaConta("Corretor 2", PerfisDoPainel.Corretor, aprovado: true);
            pendente = NovaConta("Corretor Pendente", PerfisDoPainel.Corretor, aprovado: false);
            db.Corretores.AddRange(supervisor, corretor1, corretor2, pendente);

            var cInvalida1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cInvalida1, t0.AddMinutes(-5));
            db.Conversas.Add(cInvalida1);
            db.Mensagens.Add(Mensagem.DoLead(cInvalida1.Id, "Invalida 1", t0));

            var cInvalida2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cInvalida2, Agora.AddHours(2));
            db.Conversas.Add(cInvalida2);
            db.Mensagens.Add(Mensagem.DoLead(cInvalida2.Id, "Invalida 2", t0));

            var tForaDoPeriodo = Agora.AddDays(-40);
            var cInvalida3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tForaDoPeriodo);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cInvalida3, tForaDoPeriodo.AddMinutes(10));
            db.Conversas.Add(cInvalida3);
            db.Mensagens.Add(Mensagem.DoLead(cInvalida3.Id, "Invalida 3", tForaDoPeriodo));

            var cInvalida4 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cInvalida4, t0.AddMinutes(10));
            db.Conversas.Add(cInvalida4);
            db.Mensagens.Add(Mensagem.DaLia(cInvalida4.Id, "Saudacao isolada", ProximasAcoes.ContinuarConversa, t0, []));

            var cInvalida5 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            db.Conversas.Add(cInvalida5);
            db.Mensagens.Add(Mensagem.DoLead(cInvalida5.Id, "Invalida 5", t0));

            var cValida1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cValida1, t0.AddMinutes(10));
            db.Conversas.Add(cValida1);
            db.Mensagens.Add(Mensagem.DoLead(cValida1.Id, "Valida 1", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(cValida1.Id, cValida1.LeadId, corretor1.Id, Especialidades.Moradia, t0));

            var cValida2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t0);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(cValida2, t0.AddMinutes(30));
            db.Conversas.Add(cValida2);
            db.Mensagens.Add(Mensagem.DoLead(cValida2.Id, "Valida 2", t0));
            db.Encaminhamentos.Add(Encaminhamento.Novo(cValida2.Id, cValida2.LeadId, corretor2.Id, Especialidades.Moradia, t0));

            await db.SaveChangesAsync();
        }

        var token1 = await CriarSessaoAsync(fixture.Factory.Services, corretor1);
        var metricas1 = await ObterMetricasAsync(token1);
        Assert.Equal(10.0, metricas1.Extras.TempoMedianoMin);

        var token2 = await CriarSessaoAsync(fixture.Factory.Services, corretor2);
        var metricas2 = await ObterMetricasAsync(token2);
        Assert.Equal(30.0, metricas2.Extras.TempoMedianoMin);

        var tokenSup = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricasSup = await ObterMetricasAsync(tokenSup);
        Assert.Equal(20.0, metricasSup.Extras.TempoMedianoMin);

        var tokenPendente = await CriarSessaoAsync(fixture.Factory.Services, pendente);
        var metricasPendente = await ObterMetricasAsync(tokenPendente);
        Assert.Null(metricasPendente.Extras.TempoMedianoMin);
        Assert.Empty(metricasPendente.Extras.TempoMedianoDiario);
        Assert.Equal(0, metricasPendente.DadosEssenciaisPreenchidos);
    }

    [Fact]
    public async Task Follow_up_com_quatro_conversas_calcula_com_follow_up_janela_encerrada_responderam_e_em_observacao()
    {
        await LimparSchemaAsync();

        Corretor supervisor;
        Guid cAId;
        var tA = Agora.AddDays(-15);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            db.Corretores.Add(supervisor);

            var cA = Conversa.Nova(Guid.NewGuid(), Canais.Web, tA.AddDays(-1));
            cAId = cA.Id;
            db.Conversas.Add(cA);
            db.Mensagens.Add(Mensagem.DoLead(cA.Id, "Antes do follow", tA.AddDays(-1)));

            var turnoFollow1 = new TurnoResponse(
                Resposta: "Follow 1",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            var turnoFollow2 = new TurnoResponse(
                Resposta: "Follow 2",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            cA.RegistrarFollowUp(turnoFollow1, tA);
            cA.RegistrarFollowUp(turnoFollow2, tA.AddDays(1));

            Assert.Equal(tA, cA.PrimeiroReengajamentoEm);
            Assert.Equal(2, cA.TentativasReengajamento);

            db.Mensagens.Add(Mensagem.DoLead(cA.Id, "Lead responde 1", tA.AddDays(2)));
            db.Mensagens.Add(Mensagem.DoLead(cA.Id, "Lead responde 2", tA.AddDays(3)));

            var tB = Agora.AddDays(-16);
            var cB = Conversa.Nova(Guid.NewGuid(), Canais.Web, tB);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cB, tB);
            db.Conversas.Add(cB);
            db.Mensagens.Add(Mensagem.DoLead(cB.Id, "Primeira mensagem", tB));
            db.Mensagens.Add(Mensagem.DoLead(cB.Id, "Resposta fora da janela", tB.AddDays(8)));

            var tC = Agora.AddDays(-2);
            var cC = Conversa.Nova(Guid.NewGuid(), Canais.Web, tC);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cC, tC);
            db.Conversas.Add(cC);
            db.Mensagens.Add(Mensagem.DoLead(cC.Id, "Primeira mensagem", tC));
            db.Mensagens.Add(Mensagem.DoLead(cC.Id, "Lead responde recente", tC.AddDays(1)));

            var tD = Agora.AddDays(-5);
            var cD = Conversa.Nova(Guid.NewGuid(), Canais.Web, tD);
            db.Conversas.Add(cD);
            db.Mensagens.Add(Mensagem.DoLead(cD.Id, "Lead sem follow", tD));

            await db.SaveChangesAsync();
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var cAPersistida = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == cAId);
            Assert.Equal(tA, cAPersistida.PrimeiroReengajamentoEm);
            Assert.Equal(2, cAPersistida.TentativasReengajamento);
        }

        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(token);

        Assert.Equal(7, metricas.Extras.FollowUp.JanelaDias);
        Assert.Equal(3, metricas.Extras.FollowUp.ComFollowUp);
        Assert.Equal(2, metricas.Extras.FollowUp.JanelaEncerrada);
        Assert.Equal(1, metricas.Extras.FollowUp.Responderam);
        Assert.Equal(1, metricas.Extras.FollowUp.EmObservacao);
    }

    [Fact]
    public async Task Limites_exatos_de_janela_e_instantes_de_resposta_no_follow_up()
    {
        await LimparSchemaAsync();

        Corretor supervisor;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            db.Corretores.Add(supervisor);

            var t1 = Agora.AddDays(-7);
            var c1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t1);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c1, t1);
            db.Conversas.Add(c1);
            db.Mensagens.Add(Mensagem.DoLead(c1.Id, "Inicio 1", t1));
            db.Mensagens.Add(Mensagem.DoLead(c1.Id, "Exatamente no fim da janela", Agora));

            var t2 = Agora.AddDays(-7);
            var c2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t2);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c2, t2);
            db.Conversas.Add(c2);
            db.Mensagens.Add(Mensagem.DoLead(c2.Id, "Inicio 2", t2));
            db.Mensagens.Add(Mensagem.DoLead(c2.Id, "Exatamente no envio do follow up", t2));

            var t3 = Agora.AddDays(-7);
            var c3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t3);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c3, t3);
            db.Conversas.Add(c3);
            db.Mensagens.Add(Mensagem.DoLead(c3.Id, "Inicio 3", t3));
            db.Mensagens.Add(Mensagem.DoLead(c3.Id, "1 ms apos o fim da janela", Agora.AddMilliseconds(1)));

            var t4 = Agora.AddDays(-1);
            var c4 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t4);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c4, t4);
            db.Conversas.Add(c4);
            db.Mensagens.Add(Mensagem.DoLead(c4.Id, "Inicio 4", t4));

            await db.SaveChangesAsync();
        }

        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(token);

        Assert.Equal(4, metricas.Extras.FollowUp.ComFollowUp);
        Assert.Equal(3, metricas.Extras.FollowUp.JanelaEncerrada);
        Assert.Equal(1, metricas.Extras.FollowUp.Responderam);
        Assert.Equal(1, metricas.Extras.FollowUp.EmObservacao);

        await LimparSchemaAsync();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            db.Corretores.Add(supervisor);

            var tRecente = Agora.AddDays(-1);
            var cRecente = Conversa.Nova(Guid.NewGuid(), Canais.Web, tRecente);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cRecente, tRecente);
            db.Conversas.Add(cRecente);
            db.Mensagens.Add(Mensagem.DoLead(cRecente.Id, "Inicio", tRecente));
            await db.SaveChangesAsync();
        }

        var tokenRecente = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricasRecente = await ObterMetricasAsync(tokenRecente);
        Assert.Equal(1, metricasRecente.Extras.FollowUp.ComFollowUp);
        Assert.Equal(0, metricasRecente.Extras.FollowUp.JanelaEncerrada);
        Assert.Equal(0, metricasRecente.Extras.FollowUp.Responderam);
        Assert.Equal(1, metricasRecente.Extras.FollowUp.EmObservacao);
    }

    [Fact]
    public async Task Alteracao_dinamica_de_janela_recalcula_encerradas_e_resposta()
    {
        await LimparSchemaAsync();

        Corretor supervisor;
        Corretor pendente;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            pendente = NovaConta("Pendente S45", PerfisDoPainel.Corretor, aprovado: false);
            db.Corretores.AddRange(supervisor, pendente);

            var tA = Agora.AddDays(-15);
            var cA = Conversa.Nova(Guid.NewGuid(), Canais.Web, tA);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cA, tA);
            db.Conversas.Add(cA);
            db.Mensagens.Add(Mensagem.DoLead(cA.Id, "Lead A", tA));
            db.Mensagens.Add(Mensagem.DoLead(cA.Id, "Resposta A", tA.AddHours(12)));

            var tB = Agora.AddDays(-16);
            var cB = Conversa.Nova(Guid.NewGuid(), Canais.Web, tB);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cB, tB);
            db.Conversas.Add(cB);
            db.Mensagens.Add(Mensagem.DoLead(cB.Id, "Lead B", tB));
            db.Mensagens.Add(Mensagem.DoLead(cB.Id, "Resposta B", tB.AddDays(8)));

            var tC = Agora.AddDays(-2);
            var cC = Conversa.Nova(Guid.NewGuid(), Canais.Web, tC);
            typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cC, tC);
            db.Conversas.Add(cC);
            db.Mensagens.Add(Mensagem.DoLead(cC.Id, "Lead C", tC));
            db.Mensagens.Add(Mensagem.DoLead(cC.Id, "Resposta C apos 30h", tC.AddHours(30)));

            var tD = Agora.AddDays(-5);
            var cD = Conversa.Nova(Guid.NewGuid(), Canais.Web, tD);
            db.Conversas.Add(cD);
            db.Mensagens.Add(Mensagem.DoLead(cD.Id, "Lead D", tD));

            await db.SaveChangesAsync();
        }

        var config = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        var valorOriginal = config["Metricas:JanelaRespostaFollowUpDias"];

        try
        {
            var tokenSup = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
            var metricas7 = await ObterMetricasAsync(tokenSup);

            Assert.Equal(7, metricas7.Extras.FollowUp.JanelaDias);
            Assert.Equal(3, metricas7.Extras.FollowUp.ComFollowUp);
            Assert.Equal(2, metricas7.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(1, metricas7.Extras.FollowUp.Responderam);
            Assert.Equal(1, metricas7.Extras.FollowUp.EmObservacao);

            config["Metricas:JanelaRespostaFollowUpDias"] = "1";
            var metricas1 = await ObterMetricasAsync(tokenSup);

            Assert.Equal(1, metricas1.Extras.FollowUp.JanelaDias);
            Assert.Equal(3, metricas1.Extras.FollowUp.ComFollowUp);
            Assert.Equal(3, metricas1.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(1, metricas1.Extras.FollowUp.Responderam);
            Assert.Equal(0, metricas1.Extras.FollowUp.EmObservacao);

            var tokenPendente = await CriarSessaoAsync(fixture.Factory.Services, pendente);
            var metricasPendente = await ObterMetricasAsync(tokenPendente);

            Assert.Equal(1, metricasPendente.Extras.FollowUp.JanelaDias);
            Assert.Equal(0, metricasPendente.Extras.FollowUp.ComFollowUp);
            Assert.Equal(0, metricasPendente.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(0, metricasPendente.Extras.FollowUp.Responderam);
            Assert.Equal(0, metricasPendente.Extras.FollowUp.EmObservacao);
        }
        finally
        {
            config["Metricas:JanelaRespostaFollowUpDias"] = valorOriginal;
        }
    }

    [Fact]
    public async Task Historico_parcial_e_pii_preservam_metricas_e_isolamento_por_corretor()
    {
        await LimparSchemaAsync();

        const string nomeCanario = "Lead Canario 3B";
        const string telefoneCanario = "11988889999";
        const string emailCanario = "canario3b@solar.local";
        const string textoCanario = "Segredo confidencial 3B";

        var corte = Agora.AddDays(-10);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, corte));
        }

        Corretor supervisor;
        Corretor corretor1;
        Corretor corretor2;

        try
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
                corretor1 = NovaConta("Corretor 1", PerfisDoPainel.Corretor, aprovado: true);
                corretor2 = NovaConta("Corretor 2", PerfisDoPainel.Corretor, aprovado: true);
                db.Corretores.AddRange(supervisor, corretor1, corretor2);

                var tAntesDoCorte = Agora.AddDays(-15);
                var cAntiga = Conversa.Nova(Guid.NewGuid(), Canais.Web, tAntesDoCorte);
                typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(cAntiga, corte);
                typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(cAntiga, corte.AddHours(1));
                cAntiga.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), tAntesDoCorte);
                cAntiga.Lead.RegistrarContato(nomeCanario, telefoneCanario, emailCanario, tAntesDoCorte);
                db.Conversas.Add(cAntiga);
                db.Mensagens.Add(Mensagem.DoLead(cAntiga.Id, textoCanario, tAntesDoCorte));
                db.Mensagens.Add(Mensagem.DoLead(cAntiga.Id, "Resposta dentro da janela", corte.AddDays(2)));
                db.Encaminhamentos.Add(Encaminhamento.Novo(cAntiga.Id, cAntiga.LeadId, corretor1.Id, Especialidades.Moradia, corte));

                var tDepoisDoCorte = Agora.AddDays(-8);
                var c2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tDepoisDoCorte);
                typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c2, tDepoisDoCorte);
                typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(c2, tDepoisDoCorte.AddHours(1));
                db.Conversas.Add(c2);
                db.Mensagens.Add(Mensagem.DoLead(c2.Id, "Primeira C2", tDepoisDoCorte));
                db.Encaminhamentos.Add(Encaminhamento.Novo(c2.Id, c2.LeadId, corretor1.Id, Especialidades.Moradia, tDepoisDoCorte));

                var c3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tDepoisDoCorte);
                typeof(Conversa).GetProperty(nameof(Conversa.PrimeiroReengajamentoEm))!.SetValue(c3, tDepoisDoCorte);
                db.Conversas.Add(c3);
                db.Mensagens.Add(Mensagem.DoLead(c3.Id, "Primeira C3", tDepoisDoCorte));
                db.Mensagens.Add(Mensagem.DoLead(c3.Id, "Resposta C3", tDepoisDoCorte.AddHours(10)));
                db.Encaminhamentos.Add(Encaminhamento.Novo(c3.Id, c3.LeadId, corretor2.Id, Especialidades.Moradia, tDepoisDoCorte));

                await db.SaveChangesAsync();
            }

            var tokenSup = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
            using var clientSup = Cliente(tokenSup);
            using var respostaSup = await clientSup.GetAsync("/api/painel/metricas?dias=30");
            Assert.Equal(HttpStatusCode.OK, respostaSup.StatusCode);
            var jsonBruto = await respostaSup.Content.ReadAsStringAsync();

            Assert.DoesNotContain(nomeCanario, jsonBruto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(telefoneCanario, jsonBruto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(emailCanario, jsonBruto, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(textoCanario, jsonBruto, StringComparison.OrdinalIgnoreCase);

            var metricasSup = (await respostaSup.Content.ReadFromJsonAsync<MetricasPainelResponse>())!;
            Assert.Equal(2, metricasSup.Extras.FollowUp.Responderam);
            Assert.Equal(3, metricasSup.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(3, metricasSup.Extras.FollowUp.ComFollowUp);
            Assert.Equal(0, metricasSup.Extras.FollowUp.EmObservacao);
            Assert.Equal(2, metricasSup.Avanco.Single(a => a.Etapa == "iniciadas").Conversas);
            Assert.Equal(1, metricasSup.Avanco.Single(a => a.Etapa == "essenciais").Conversas);
            Assert.Equal(1, metricasSup.DadosEssenciaisPreenchidos);

            var token1 = await CriarSessaoAsync(fixture.Factory.Services, corretor1);
            var metricas1 = await ObterMetricasAsync(token1);
            Assert.Equal(1, metricas1.Extras.FollowUp.Responderam);
            Assert.Equal(2, metricas1.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(2, metricas1.Extras.FollowUp.ComFollowUp);
            Assert.Equal(0, metricas1.Extras.FollowUp.EmObservacao);
            Assert.Equal(1, metricas1.Avanco.Single(a => a.Etapa == "atribuidas").Conversas);
            Assert.Equal(1, metricas1.DadosEssenciaisPreenchidos);

            var token2 = await CriarSessaoAsync(fixture.Factory.Services, corretor2);
            var metricas2 = await ObterMetricasAsync(token2);
            Assert.Equal(1, metricas2.Extras.FollowUp.JanelaEncerrada);
            Assert.Equal(1, metricas2.Extras.FollowUp.Responderam);
            Assert.Equal(1, metricas2.Extras.FollowUp.ComFollowUp);
            Assert.Equal(0, metricas2.Extras.FollowUp.EmObservacao);
            Assert.Equal(1, metricas2.Avanco.Single(a => a.Etapa == "atribuidas").Conversas);
            Assert.Equal(0, metricas2.DadosEssenciaisPreenchidos);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, MetricasPainelPostgresFixture.HistoricoDesde));
        }
    }
}
