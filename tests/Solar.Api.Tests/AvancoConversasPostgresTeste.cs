using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class AvancoConversasPostgresTeste(MetricasPainelPostgresFixture fixture)
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
    public async Task Base_vazia_supervisor_retorna_seis_etapas_com_zeros_e_sem_essenciais_em_encaminhamento()
    {
        await LimparSchemaAsync();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
        db.Corretores.Add(supervisor);
        await db.SaveChangesAsync();
        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);

        var metricas = await ObterMetricasAsync(token);

        Assert.Equal(MetricasPainelPostgresFixture.HistoricoDesde, metricas.Periodo.HistoricoDesde);
        Assert.Equal(0, metricas.ConversasIniciadas);
        Assert.Equal(0, metricas.HorariosConfirmados);

        Assert.NotNull(metricas.Avanco);
        Assert.Equal(6, metricas.Avanco.Count);

        var etapasEsperadas = new[] { "iniciadas", "intencao", "essenciais", "encaminhamento", "corretor", "horario" };
        Assert.Equal(etapasEsperadas, metricas.Avanco.Select(a => a.Etapa));

        foreach (var item in metricas.Avanco)
        {
            Assert.Equal(0, item.Conversas);
            if (item.Etapa == "encaminhamento")
            {
                Assert.Equal(0, item.SemEssenciais);
            }
            else
            {
                Assert.Null(item.SemEssenciais);
            }
        }

        Assert.Null(metricas.Extras.TempoMedianoMin);
        Assert.Empty(metricas.Extras.TempoMedianoDiario);
        Assert.NotNull(metricas.Extras.FollowUp);
        Assert.Equal(7, metricas.Extras.FollowUp.JanelaDias);
        Assert.Equal(0, metricas.Extras.FollowUp.ComFollowUp);
        Assert.Equal(0, metricas.Extras.FollowUp.JanelaEncerrada);
        Assert.Equal(0, metricas.Extras.FollowUp.Responderam);
        Assert.Equal(0, metricas.Extras.FollowUp.EmObservacao);
    }

    [Fact]
    public async Task Barras_independentes_calculam_etapas_corretamente_sem_depender_de_etapa_anterior()
    {
        await LimparSchemaAsync();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();

        var supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
        var corretor = NovaConta("Corretor S45", PerfisDoPainel.Corretor, aprovado: true);
        db.Corretores.AddRange(supervisor, corretor);
        await db.SaveChangesAsync();

        var tStart = Agora.AddDays(-10);

        // Conversa A: intencao, encaminhamento, corretor, horario (duas mensagens de agendamento), SEM essenciais
        var conversaA = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
        conversaA.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), tStart);
        typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversaA, tStart.AddHours(1));
        typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversaA, tStart.AddHours(2));
        typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversaA, tStart.AddHours(3));
        db.Conversas.Add(conversaA);
        db.Mensagens.Add(Mensagem.DoLead(conversaA.Id, "Ola A", tStart));

        var msgConfirmada1 = Mensagem.DaLia(conversaA.Id, "Agendado 1", ProximasAcoes.ContinuarConversa, tStart.AddHours(4), []);
        msgConfirmada1.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
        var msgConfirmada2 = Mensagem.DaLia(conversaA.Id, "Agendado 2", ProximasAcoes.ContinuarConversa, tStart.AddHours(5), []);
        msgConfirmada2.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
        db.Mensagens.AddRange(msgConfirmada1, msgConfirmada2);

        // Conversa B: apenas essenciais (com intencao indefinida), sem encaminhamento
        var conversaB = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
        typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversaB, tStart.AddHours(1));
        db.Conversas.Add(conversaB);
        db.Mensagens.Add(Mensagem.DoLead(conversaB.Id, "Ola B", tStart));

        // Conversa C: intencao e encaminhamento antes de preencher essenciais posteriormente
        var conversaC = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
        conversaC.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), tStart);
        typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversaC, tStart.AddHours(1));
        typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversaC, tStart.AddHours(2));
        typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversaC, tStart.AddHours(3));
        db.Conversas.Add(conversaC);
        db.Mensagens.Add(Mensagem.DoLead(conversaC.Id, "Ola C", tStart));

        await db.SaveChangesAsync();

        var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(token);

        Assert.Equal(3, metricas.ConversasIniciadas);
        Assert.Equal(1, metricas.HorariosConfirmados);

        var avanco = metricas.Avanco.ToDictionary(a => a.Etapa);
        Assert.Equal(3, avanco["iniciadas"].Conversas);
        Assert.Equal(2, avanco["intencao"].Conversas);
        Assert.Equal(2, avanco["essenciais"].Conversas);
        Assert.Equal(2, avanco["encaminhamento"].Conversas);
        Assert.Equal(2, avanco["encaminhamento"].SemEssenciais);
        Assert.Equal(1, avanco["corretor"].Conversas);
        Assert.Equal(1, avanco["horario"].Conversas);
    }

    [Fact]
    public async Task Corte_exato_de_historico_desde_inclui_limite_e_posterior_e_exclui_anterior()
    {
        await LimparSchemaAsync();

        var corte = Agora.AddDays(-15);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, corte));
        }

        Corretor supervisor;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            var corretor = NovaConta("Corretor S45", PerfisDoPainel.Corretor, aprovado: true);
            db.Corretores.AddRange(supervisor, corretor);

            // Conversa 1: antes do corte (1 ms antes)
            var t1 = corte.AddMilliseconds(-1);
            var conversa1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t1);
            conversa1.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), t1);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversa1, t1);
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversa1, t1);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversa1, t1);
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversa1, t1);
            db.Conversas.Add(conversa1);
            db.Mensagens.Add(Mensagem.DaLia(conversa1.Id, "Ola saudacao", ProximasAcoes.ContinuarConversa, corte.AddDays(-2), []));
            db.Mensagens.Add(Mensagem.DoLead(conversa1.Id, "Resposta lead", t1));
            var msgConf1 = Mensagem.DaLia(conversa1.Id, "Conf 1", ProximasAcoes.ContinuarConversa, t1, []);
            msgConf1.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(msgConf1);

            // Conversa 2: instante exato do corte
            var t2 = corte;
            var conversa2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t2);
            conversa2.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), t2);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversa2, t2);
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversa2, t2);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversa2, t2);
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversa2, t2);
            db.Conversas.Add(conversa2);
            db.Mensagens.Add(Mensagem.DaLia(conversa2.Id, "Ola saudacao", ProximasAcoes.ContinuarConversa, corte.AddDays(-1), []));
            db.Mensagens.Add(Mensagem.DoLead(conversa2.Id, "Resposta lead", t2));
            var msgConf2 = Mensagem.DaLia(conversa2.Id, "Conf 2", ProximasAcoes.ContinuarConversa, t2, []);
            msgConf2.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(msgConf2);

            // Conversa 3: 1 ms depois do corte
            var t3 = corte.AddMilliseconds(1);
            var conversa3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, t3);
            conversa3.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), t3);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversa3, t3);
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversa3, t3);
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversa3, t3);
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversa3, t3);
            db.Conversas.Add(conversa3);
            db.Mensagens.Add(Mensagem.DoLead(conversa3.Id, "Resposta lead", t3));
            var msgConf3 = Mensagem.DaLia(conversa3.Id, "Conf 3", ProximasAcoes.ContinuarConversa, t3, []);
            msgConf3.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(msgConf3);

            // Conversa 4: sem mensagem do lead (apenas saudacao da Lia)
            var conversa4 = Conversa.Nova(Guid.NewGuid(), Canais.Web, corte);
            db.Conversas.Add(conversa4);
            db.Mensagens.Add(Mensagem.DaLia(conversa4.Id, "Saudacao isolada", ProximasAcoes.ContinuarConversa, corte, []));

            await db.SaveChangesAsync();
        }

        try
        {
            var token = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
            var metricas = await ObterMetricasAsync(token);

            Assert.Equal(corte, metricas.Periodo.HistoricoDesde);
            Assert.Equal(3, metricas.ConversasIniciadas);
            Assert.Equal(2, metricas.HorariosConfirmados);

            var avanco = metricas.Avanco.ToDictionary(a => a.Etapa);
            Assert.Equal(2, avanco["iniciadas"].Conversas);
            Assert.Equal(2, avanco["intencao"].Conversas);
            Assert.Equal(2, avanco["essenciais"].Conversas);
            Assert.Equal(2, avanco["encaminhamento"].Conversas);
            Assert.Equal(2, avanco["corretor"].Conversas);
            Assert.Equal(2, avanco["horario"].Conversas);

            Assert.Equal(3, metricas.LeadsPorIntencao.Compra);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, MetricasPainelPostgresFixture.HistoricoDesde));
        }
    }

    [Fact]
    public async Task Supervisor_estavel_apos_redistribuicao_preserva_avanco_e_atualiza_distribuicao_da_equipe()
    {
        await LimparSchemaAsync();

        Corretor corretorA;
        Corretor corretorB;
        Corretor supervisor;
        Guid conversaId;
        var tStart = Agora.AddDays(-5);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            corretorA = NovaConta("Corretor A", PerfisDoPainel.Corretor, aprovado: true, ["Centro"]);
            db.Corretores.AddRange(supervisor, corretorA);

            conversaId = Guid.NewGuid();
            var conversa = Conversa.Nova(conversaId, Canais.Web, tStart);
            conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: "Centro"), tStart);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversa, tStart.AddHours(1));
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversa, tStart.AddHours(1));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversa, tStart.AddHours(2));
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversa, tStart.AddHours(3));

            db.Conversas.Add(conversa);
            db.Mensagens.Add(Mensagem.DoLead(conversa.Id, "Ola", tStart));

            var enc = Encaminhamento.Novo(conversa.Id, conversa.LeadId, corretorA.Id, Especialidades.Moradia, tStart.AddHours(3));
            db.Encaminhamentos.Add(enc);

            await db.SaveChangesAsync();
        }

        var supervisorToken = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricasAntes = await ObterMetricasAsync(supervisorToken);

        Assert.Equal(1, metricasAntes.Avanco.Single(a => a.Etapa == "corretor").Conversas);
        var itemAntes = Assert.Single(metricasAntes.Equipe!.AtribuidasPorCorretor);
        Assert.Equal(corretorA.Nome, itemAntes.Corretor.Nome);
        Assert.Equal(1, itemAntes.Conversas);
        Assert.Equal(0, metricasAntes.Equipe.AguardandoCorretor);

        // Redistribui para B
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var cA = await db.Corretores.SingleAsync(c => c.Id == corretorA.Id);
            cA.Desativar();

            corretorB = NovaConta("Corretor B", PerfisDoPainel.Corretor, aprovado: true, ["Centro"]);
            db.Corretores.Add(corretorB);
            await db.SaveChangesAsync();

            var encRepo = new EncaminhamentoRepositorio(db);
            await encRepo.RedistribuirAsync(corretorA.Id, Agora.AddHours(-1), default);
        }

        var metricasDepois = await ObterMetricasAsync(supervisorToken);

        Assert.Equal(1, metricasDepois.Avanco.Single(a => a.Etapa == "corretor").Conversas);
        var itemDepois = Assert.Single(metricasDepois.Equipe!.AtribuidasPorCorretor);
        Assert.Equal(corretorB.Nome, itemDepois.Corretor.Nome);
        Assert.Equal(1, itemDepois.Conversas);
        Assert.Equal(0, metricasDepois.Equipe.AguardandoCorretor);

        // Desatribui sem substituto (desativa B e redistribui sem outros corretores ativos)
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

        Assert.Equal(1, metricasDesatribuida.Avanco.Single(a => a.Etapa == "corretor").Conversas);
        Assert.Empty(metricasDesatribuida.Equipe!.AtribuidasPorCorretor);
        Assert.Equal(1, metricasDesatribuida.Equipe.AguardandoCorretor);
    }

    [Fact]
    public async Task Corretor_recebe_apenas_conversas_proprias_e_corretor_pendente_recebe_metricas_zeradas()
    {
        await LimparSchemaAsync();

        Corretor corretor1;
        Corretor corretor2;
        Corretor pendente;
        Corretor supervisor;

        var tStart = Agora.AddDays(-5);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            corretor1 = NovaConta("Corretor 1", PerfisDoPainel.Corretor, aprovado: true);
            corretor2 = NovaConta("Corretor 2", PerfisDoPainel.Corretor, aprovado: true);
            pendente = NovaConta("Corretor Pendente", PerfisDoPainel.Corretor, aprovado: false);
            db.Corretores.AddRange(supervisor, corretor1, corretor2, pendente);

            // Conversa 1 -> Corretor 1
            var conversa1 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
            db.Conversas.Add(conversa1);
            db.Mensagens.Add(Mensagem.DoLead(conversa1.Id, "Ola 1", tStart));
            var conf1 = Mensagem.DaLia(conversa1.Id, "Conf 1", ProximasAcoes.ContinuarConversa, tStart.AddHours(1), []);
            conf1.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(conf1);
            db.Encaminhamentos.Add(Encaminhamento.Novo(conversa1.Id, conversa1.LeadId, corretor1.Id, Especialidades.Moradia, tStart));

            // Conversa 2 -> Corretor 2
            var conversa2 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
            db.Conversas.Add(conversa2);
            db.Mensagens.Add(Mensagem.DoLead(conversa2.Id, "Ola 2", tStart));
            var conf2 = Mensagem.DaLia(conversa2.Id, "Conf 2", ProximasAcoes.ContinuarConversa, tStart.AddHours(1), []);
            conf2.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(conf2);
            db.Encaminhamentos.Add(Encaminhamento.Novo(conversa2.Id, conversa2.LeadId, corretor2.Id, Especialidades.Moradia, tStart));

            // Conversa 3 -> Pendente
            var conversa3 = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
            db.Conversas.Add(conversa3);
            db.Mensagens.Add(Mensagem.DoLead(conversa3.Id, "Ola 3", tStart));
            var conf3 = Mensagem.DaLia(conversa3.Id, "Conf 3", ProximasAcoes.ContinuarConversa, tStart.AddHours(1), []);
            db.Mensagens.Add(conf3);
            db.Encaminhamentos.Add(Encaminhamento.Novo(conversa3.Id, conversa3.LeadId, pendente.Id, Especialidades.Moradia, tStart));

            await db.SaveChangesAsync();
        }

        var tokenCorretor1 = await CriarSessaoAsync(fixture.Factory.Services, corretor1);
        var metricasCorretor1 = await ObterMetricasAsync(tokenCorretor1);

        Assert.Null(metricasCorretor1.Equipe);
        Assert.Equal(2, metricasCorretor1.Avanco.Count);
        Assert.Equal(new[] { "atribuidas", "horario" }, metricasCorretor1.Avanco.Select(a => a.Etapa));
        Assert.Equal(1, metricasCorretor1.Avanco.Single(a => a.Etapa == "atribuidas").Conversas);
        Assert.Equal(1, metricasCorretor1.Avanco.Single(a => a.Etapa == "horario").Conversas);
        Assert.Equal(1, metricasCorretor1.HorariosConfirmados);

        var tokenPendente = await CriarSessaoAsync(fixture.Factory.Services, pendente);
        var metricasPendente = await ObterMetricasAsync(tokenPendente);

        Assert.Null(metricasPendente.Equipe);
        Assert.Equal(2, metricasPendente.Avanco.Count);
        Assert.Equal(0, metricasPendente.Avanco.Single(a => a.Etapa == "atribuidas").Conversas);
        Assert.Equal(0, metricasPendente.Avanco.Single(a => a.Etapa == "horario").Conversas);
        Assert.Equal(0, metricasPendente.ConversasIniciadas);
        Assert.Equal(0, metricasPendente.HorariosConfirmados);
        Assert.Equal(MetricasPainelPostgresFixture.HistoricoDesde, metricasPendente.Periodo.HistoricoDesde);
        Assert.Equal(7, metricasPendente.Extras.FollowUp.JanelaDias);

        var tokenSupervisor = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricasSupervisor = await ObterMetricasAsync(tokenSupervisor);
        Assert.Equal(6, metricasSupervisor.Avanco.Count);
        Assert.Equal(2, metricasSupervisor.HorariosConfirmados);
    }

    [Fact]
    public async Task Perfil_completo_no_primeiro_turno_com_bool_true_grava_essenciais_em_e_metrica_essenciais_igual_1()
    {
        await LimparSchemaAsync();

        var t0 = Agora.AddDays(-5);
        Corretor supervisor;
        Guid conversaId;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            db.Corretores.Add(supervisor);

            conversaId = Guid.NewGuid();
            var conversa = Conversa.Nova(conversaId, Canais.Web, t0);

            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();

            var turno = new TurnoResponse(
                Resposta: "Entendido, dados essenciais completos.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: "Pinheiros"),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true);

            var repo = new ConversaRepositorio(db);
            repo.AplicarTurno(conversa, "Quero comprar apartamento em Pinheiros", turno, t0);
            await db.SaveChangesAsync();
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var recarregada = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(t0, recarregada.EssenciaisEm);
        }

        var supervisorToken = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(supervisorToken);

        var etapaEssenciais = metricas.Avanco.Single(a => a.Etapa == "essenciais");
        Assert.Equal(1, etapaEssenciais.Conversas);
    }

    [Fact]
    public async Task Resposta_http_nao_contem_pii_ou_transcricao_de_lead_nos_campos_novos()
    {
        await LimparSchemaAsync();

        const string nomeCanario = "Titular Canario S45";
        const string telefoneCanario = "11988887777";
        const string emailCanario = "titular-s45@canario.local";
        const string textoCanario = "Transcricao canario confidencial S45";

        var tStart = Agora.AddDays(-5);
        Corretor supervisor;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            supervisor = NovaConta("Supervisor S45", PerfisDoPainel.Supervisor, aprovado: true);
            var corretor = NovaConta("Corretor S45", PerfisDoPainel.Corretor, aprovado: true);
            db.Corretores.AddRange(supervisor, corretor);

            var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, tStart);
            conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), tStart);
            conversa.Lead.RegistrarContato(nomeCanario, telefoneCanario, emailCanario, tStart);
            typeof(Conversa).GetProperty(nameof(Conversa.IntencaoEm))!.SetValue(conversa, tStart.AddHours(1));
            typeof(Conversa).GetProperty(nameof(Conversa.EssenciaisEm))!.SetValue(conversa, tStart.AddHours(1));
            typeof(Conversa).GetProperty(nameof(Conversa.EncaminhadaEm))!.SetValue(conversa, tStart.AddHours(2));
            typeof(Conversa).GetProperty(nameof(Conversa.CorretorAtribuidoEm))!.SetValue(conversa, tStart.AddHours(3));

            db.Conversas.Add(conversa);
            db.Mensagens.Add(Mensagem.DoLead(conversa.Id, textoCanario, tStart));

            var conf = Mensagem.DaLia(conversa.Id, "Confirmado", ProximasAcoes.ContinuarConversa, tStart.AddHours(4), []);
            conf.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            db.Mensagens.Add(conf);

            db.Encaminhamentos.Add(Encaminhamento.Novo(conversa.Id, conversa.LeadId, corretor.Id, Especialidades.Moradia, tStart.AddHours(3)));

            await db.SaveChangesAsync();
        }

        var supervisorToken = await CriarSessaoAsync(fixture.Factory.Services, supervisor);
        var metricas = await ObterMetricasAsync(supervisorToken);

        var json = JsonSerializer.Serialize(metricas);

        Assert.DoesNotContain(nomeCanario, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(telefoneCanario, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(emailCanario, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(textoCanario, json, StringComparison.OrdinalIgnoreCase);
    }
}
