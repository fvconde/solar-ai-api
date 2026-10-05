using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class MetricasPainelPostgresTeste(MetricasPainelPostgresFixture fixture)
    : IClassFixture<MetricasPainelPostgresFixture>
{
    private static readonly DateTimeOffset Agora = MetricasPainelPostgresFixture.Agora;
    private const string NomeCanario = "Titular Canario Privado S22";
    private const string TelefoneCanario = "11976543210";
    private const string EmailCanario = "titular-canario-s22@tests.solar.local";
    private const string TextoCanario = "Transcricao confidencial canario S22";

    [Fact]
    public async Task Base_vazia_tem_zeros_listas_vazias_datas_iso_e_prazo_do_Development()
    {
        var cenario = await CriarCenarioAsync();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.Corretores.Where(c => c.Perfil == PerfisDoPainel.Corretor &&
                c.StatusCorretor == StatusDoCorretor.EmAnalise).ExecuteDeleteAsync();
        }

        var chamadasAntes = fixture.Relogio.Chamadas;
        using var client = Cliente(cenario.SupervisorToken);
        using var resposta = await client.GetAsync("/api/painel/metricas");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var metricas = (await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>())!;
        Assert.Equal(new PeriodoMetricasPainel(30, Agora.AddDays(-30), Agora, MetricasPainelPostgresFixture.HistoricoDesde), metricas.Periodo);
        Assert.Equal(2, fixture.Relogio.Chamadas - chamadasAntes);
        AssertZeradas(metricas);
        Assert.Equal(1, metricas.Extras.Privacidade.PrazoRetencaoMeses);
        Assert.Equal(1, fixture.PrazoInicial);
        Assert.NotNull(metricas.Equipe);
        Assert.Empty(metricas.Equipe.AtribuidasPorCorretor);
        Assert.Equal(0, metricas.Equipe.AguardandoCorretor);
        Assert.Equal(0, metricas.Equipe.PendentesAprovacao);
        var json = await resposta.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Agora, doc.RootElement.GetProperty("periodo").GetProperty("atualizadoEm").GetDateTimeOffset());
        Assert.True(doc.RootElement.TryGetProperty("avanco", out var avancoProp));
        Assert.Equal(JsonValueKind.Array, avancoProp.ValueKind);
        Assert.True(doc.RootElement.GetProperty("periodo").TryGetProperty("historicoDesde", out var historicoDesdeProp));
        Assert.Equal(MetricasPainelPostgresFixture.HistoricoDesde, historicoDesdeProp.GetDateTimeOffset());
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("%", json);
        var corretor = await ObterAsync(cenario.CorretorToken);
        AssertZeradas(corretor);
        Assert.Null(corretor.Equipe);
    }

    [Fact]
    public async Task Supervisor_confere_todos_os_numeros_por_conversa_lead_e_slot()
    {
        var cenario = await CriarCenarioCompletoAsync();
        var metricas = await ObterAsync(cenario.SupervisorToken);
        Assert.Equal(5, metricas.ConversasIniciadas);
        Assert.Equal(3, metricas.HorariosConfirmados);
        Assert.Equal(4, metricas.ReservasProximos7Dias);
        Assert.Equal(new IntencoesMetricasPainel(2, 2, 1, 2), metricas.LeadsPorIntencao);
        Assert.Equal(new ScoreMetricasPainel(2, 2, 2, 1), metricas.Extras.Score);
        Assert.Equal(7, metricas.Extras.Regioes.Leads);
        Assert.Equal(7, metricas.Extras.Regioes.Informaram);
        Assert.Equal(0, metricas.Extras.Regioes.Outras);
        Assert.Equal(
            new[] { new RegiaoMetricasPainel("CHACARA KLABIN", 2), new("MOEMA", 2),
                new("ALFA", 1), new("BETA", 1), new("GAMA", 1) },
            metricas.Extras.Regioes.Top);
        Assert.Equal(new[] { new ImovelMetricasPainel("IMV-1", "Moema", 2),
            new("IMV-2", "Vila Nova", 1), new("IMV-3", "Centro", 1),
            new("IMV-ANTIGO", "Tatuapé", 1) }, metricas.Extras.Imoveis);
        Assert.Equal(new[] { Agora, Agora.AddDays(2), Agora.AddDays(3),
            Agora.AddDays(7), Agora.AddDays(7).AddMilliseconds(1), Agora.AddDays(9) },
            metricas.Extras.ProximosHorarios.Select(s => s.Inicio));
        Assert.Equal(new[] { "HB", "CD", "HB", "HB", "HB", "CD" },
            metricas.Extras.ProximosHorarios.Select(s => s.Iniciais));
        Assert.Equal(new PrivacidadeMetricasPainel(7, 3, 1, 4, Agora.AddDays(-20).AddMonths(1)),
            metricas.Extras.Privacidade);
        var equipe = metricas.Equipe!;
        Assert.Equal(3, equipe.AguardandoCorretor);
        Assert.Equal(2, equipe.PendentesAprovacao);
        Assert.Equal(2, equipe.AtribuidasPorCorretor.Count);
        Assert.All(equipe.AtribuidasPorCorretor, atribuicao => Assert.Equal(2, atribuicao.Conversas));
        Assert.Equal(new[] { cenario.Corretor.Id, cenario.Outro.Id }.Order(),
            equipe.AtribuidasPorCorretor.Select(a => a.Corretor.Id));
        Assert.Contains(equipe.AtribuidasPorCorretor, a =>
            a.Corretor == new CorretorPainelResumo(cenario.Corretor.Id, "Helena Braga", "HB"));
        using var client = Cliente(cenario.SupervisorToken);
        var pendentes = await client.GetFromJsonAsync<List<CorretorPendente>>("/api/painel/corretores/pendentes");
        Assert.Equal(pendentes!.Count, equipe.PendentesAprovacao);
    }

    [Fact]
    public async Task Corretor_nao_recebe_conversas_snapshots_ou_slots_alheios_do_mesmo_lead()
    {
        var cenario = await CriarCenarioCompletoAsync();
        var metricas = await ObterAsync(cenario.CorretorToken);
        Assert.Null(metricas.Equipe);
        Assert.Equal(2, metricas.ConversasIniciadas);
        Assert.Equal(2, metricas.HorariosConfirmados);
        Assert.Equal(2, metricas.ReservasProximos7Dias);
        Assert.Equal(new IntencoesMetricasPainel(2, 0, 0, 0), metricas.LeadsPorIntencao);
        Assert.Equal(new ScoreMetricasPainel(2, 0, 0, 0), metricas.Extras.Score);
        Assert.Equal(new[] { new RegiaoMetricasPainel("MOEMA", 2) }, metricas.Extras.Regioes.Top);
        Assert.Equal(2, metricas.Extras.Regioes.Informaram);
        Assert.Equal(2, metricas.Extras.Regioes.Leads);
        Assert.Equal(0, metricas.Extras.Regioes.Outras);
        Assert.Equal(new[] { new ImovelMetricasPainel("IMV-1", "Moema", 1),
            new("IMV-3", "Centro", 1) }, metricas.Extras.Imoveis);
        Assert.Equal(new[] { Agora, Agora.AddDays(7), Agora.AddDays(7).AddMilliseconds(1) },
            metricas.Extras.ProximosHorarios.Select(s => s.Inicio));
        Assert.All(metricas.Extras.ProximosHorarios, horario => Assert.Equal("HB", horario.Iniciais));
        Assert.Equal(new PrivacidadeMetricasPainel(2, 2, 1, 2, Agora.AddDays(-3).AddMonths(1)),
            metricas.Extras.Privacidade);
    }

    [Fact]
    public async Task Redistribuicao_e_empate_Em_Id_mudam_o_recorte_e_equipe_sem_historico()
    {
        var cenario = await CriarCenarioCompletoAsync();
        var antes = await ObterAsync(cenario.CorretorToken);
        Assert.Equal(2, antes.ConversasIniciadas);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var encaminhamento = await db.Encaminhamentos.SingleAsync(e => e.Id == cenario.UltimoEncaminhamentoId);
        encaminhamento.Atribuir(cenario.Outro.Id, Agora);
        await db.SaveChangesAsync();
        var depois = await ObterAsync(cenario.CorretorToken);
        Assert.Equal(1, depois.ConversasIniciadas);
        Assert.Equal(1, depois.HorariosConfirmados);
        Assert.Equal(0, depois.ReservasProximos7Dias);
        Assert.Equal(1, depois.Extras.Privacidade.Leads);
        Assert.Equal(new[] { new ImovelMetricasPainel("IMV-3", "Centro", 1) }, depois.Extras.Imoveis);
        var supervisor = await ObterAsync(cenario.SupervisorToken);
        Assert.Equal(5, supervisor.ConversasIniciadas);
        Assert.Equal(3, supervisor.HorariosConfirmados);
        Assert.Equal(1, supervisor.Equipe!.AtribuidasPorCorretor.Single(a =>
            a.Corretor.Id == cenario.Corretor.Id).Conversas);
        Assert.Equal(3, supervisor.Equipe.AtribuidasPorCorretor.Single(a =>
            a.Corretor.Id == cenario.Outro.Id).Conversas);
        Assert.Equal(3, supervisor.Equipe.AguardandoCorretor);
    }

    [Fact]
    public async Task Corretor_em_analise_recebe_tudo_zerado_mesmo_com_atribuicao_e_reserva()
    {
        var cenario = await CriarCenarioCompletoAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var encaminhamento = await db.Encaminhamentos.SingleAsync(e => e.Id == cenario.UltimoEncaminhamentoId);
        encaminhamento.Atribuir(cenario.Pendente.Id, Agora);
        var slot = Slot.Novo(cenario.Pendente.Id, Agora, Agora.AddHours(1));
        db.Slots.Add(slot);
        db.Entry(slot).Property(s => s.LeadId).CurrentValue = encaminhamento.LeadId;
        await db.SaveChangesAsync();
        var metricas = await ObterAsync(cenario.PendenteToken);
        AssertZeradas(metricas);
        Assert.Null(metricas.Equipe);
        Assert.Equal(1, metricas.Extras.Privacidade.PrazoRetencaoMeses);
    }

    [Fact]
    public async Task Resposta_http_serializada_nao_contem_pii_ou_transcricao_de_lead()
    {
        var cenario = await CriarCenarioCompletoAsync();
        foreach (var token in new[] { cenario.SupervisorToken, cenario.CorretorToken })
        {
            using var client = Cliente(token);
            using var resposta = await client.GetAsync("/api/painel/metricas?dias=30");
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var json = await resposta.Content.ReadAsStringAsync();
            foreach (var canario in new[] { NomeCanario, TelefoneCanario, EmailCanario, TextoCanario })
            {
                Assert.DoesNotContain(canario, json, StringComparison.OrdinalIgnoreCase);
            }

            using var doc = JsonDocument.Parse(json);
            Assert.Equal(new[] { "avanco", "conversasIniciadas", "equipe", "extras", "horariosConfirmados",
                "leadsPorIntencao", "periodo", "reservasProximos7Dias" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            foreach (var nome in new[] { "nome", "telefone", "email", "texto", "leadId", "conversaId" })
            {
                Assert.DoesNotContain('"' + nome + '"', doc.RootElement.GetProperty("extras").GetRawText());
            }

            if (token == cenario.SupervisorToken)
            {
                Assert.Contains("Helena Braga", json);
                var horarios = doc.RootElement.GetProperty("extras").GetProperty("proximosHorarios");
                Assert.All(horarios.EnumerateArray(), horario =>
                    Assert.Equal(new[] { "iniciais", "inicio" },
                        horario.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("token-invalido")]
    public async Task Sem_sessao_valida_recebe_401(string? token)
    {
        await CriarCenarioAsync();
        using var client = Cliente(token);
        using var resposta = await client.GetAsync("/api/painel/metricas");
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
    }

    [Fact]
    public async Task Cliente_recebe_403()
    {
        var cenario = await CriarCenarioCompletoAsync();
        using var client = Cliente(cenario.ClienteToken);
        using var resposta = await client.GetAsync("/api/painel/metricas");
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.DoesNotContain("conversasIniciadas", await resposta.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    [InlineData("")]
    public async Task Dias_invalidos_recebem_400(string dias)
    {
        var cenario = await CriarCenarioAsync();
        using var client = Cliente(cenario.SupervisorToken);
        using var resposta = await client.GetAsync("/api/painel/metricas?dias=" + dias);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 5)]
    [InlineData(365, 6)]
    public async Task Periodo_valido_usa_primeira_mensagem_e_inclui_limites(int dias, int esperado)
    {
        var cenario = await CriarCenarioCompletoAsync();
        using var client = Cliente(cenario.SupervisorToken);
        var metricas = await client.GetFromJsonAsync<MetricasPainelResponse>("/api/painel/metricas?dias=" + dias);
        Assert.Equal(esperado, metricas!.ConversasIniciadas);
        Assert.Equal(Agora.AddDays(-dias), metricas.Periodo.Inicio);
    }

    [Fact]
    public async Task Regioes_top_cinco_sobra_cobertura_e_desempate_sao_deterministicos()
    {
        var cenario = await CriarCenarioAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        foreach (var regiao in new[] { "moéma", "MOEMA", "álfa", "BETA", "café", "Delta",
            "Épsilon", "zeta", null, "  " })
        {
            NovoLead(db, Agora, Intencoes.Indefinida, null, regiao);
        }

        await db.SaveChangesAsync();
        var metricas = await ObterAsync(cenario.SupervisorToken);
        Assert.Equal(new[] { new RegiaoMetricasPainel("MOEMA", 2), new("ALFA", 1), new("BETA", 1),
            new("CAFE", 1), new("DELTA", 1) }, metricas.Extras.Regioes.Top);
        Assert.Equal(2, metricas.Extras.Regioes.Outras);
        Assert.Equal(8, metricas.Extras.Regioes.Informaram);
        Assert.Equal(10, metricas.Extras.Regioes.Leads);
        Assert.Equal(0, metricas.ConversasIniciadas);
        Assert.Equal(10, metricas.LeadsPorIntencao.SemIntencao);
    }

    [Fact]
    public async Task Privacidade_preserva_fallback_followup_e_limites_inclusivos()
    {
        var cenario = await CriarCenarioAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var semConversa = NovoLead(db, Agora.AddMonths(-1), null, null, null);
        var criadoAntes = NovoLead(db, Agora.AddMonths(-1).AddDays(1), null, null, null);
        NovaConversa(db, criadoAntes, Agora, null, null, false);
        var conversaAntes = NovoLead(db, Agora, null, null, null);
        NovaConversa(db, conversaAntes, Agora.AddMonths(-1).AddDays(2), null, null, false);
        var comMensagem = NovoLead(db, Agora.AddYears(-3), null, null, null);
        var primeira = NovaConversa(db, comMensagem, Agora.AddYears(-2),
            Agora.AddMonths(-1).AddDays(3), null, false);
        NovaConversa(db, comMensagem, Agora.AddMonths(-1), Agora.AddMonths(-1).AddDays(4), null, false);
        db.Mensagens.Add(Mensagem.DaLia(primeira.Id, TextoCanario, ProximasAcoes.ContinuarConversa, Agora));
        NovoLead(db, Agora.AddDays(30).AddMonths(-1), null, null, null);
        NovoLead(db, Agora.AddDays(30).AddMonths(-1).AddMilliseconds(1), null, null, null);
        NovoLead(db, Agora.AddMonths(-1).AddMilliseconds(-1), null, null, null);
        semConversa.RegistrarConsentimento("s22", Agora);
        await db.SaveChangesAsync();
        var metricas = await ObterAsync(cenario.SupervisorToken);
        Assert.Equal(new PrivacidadeMetricasPainel(7, 1, 1, 5, Agora), metricas.Extras.Privacidade);
        Assert.Equal(2, metricas.ConversasIniciadas);
    }

    [Fact]
    public async Task Prazo_configurado_muda_vencimento_sem_hardcode_e_base_so_vencida_tem_proximo_nulo()
    {
        var cenario = await CriarCenarioAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        NovoLead(db, Agora.AddMonths(-2), null, null, null);
        await db.SaveChangesAsync();
        var antes = await ObterAsync(cenario.SupervisorToken);
        Assert.Null(antes.Extras.Privacidade.ProximoVencimento);
        Assert.Equal(0, antes.Extras.Privacidade.Vencem30Dias);
        var configuracao = fixture.Factory.Services.GetRequiredService<IConfiguration>();
        configuracao["Expurgo:PrazoRetencaoMeses"] = "2";
        var depois = await ObterAsync(cenario.SupervisorToken);
        Assert.Equal(new PrivacidadeMetricasPainel(1, 0, 2, 1, Agora), depois.Extras.Privacidade);
    }

    private async Task<Cenario> CriarCenarioAsync()
    {
        fixture.Factory.Services.GetRequiredService<IConfiguration>()["Expurgo:PrazoRetencaoMeses"] =
            fixture.PrazoInicial.ToString();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        await db.Conversas.ExecuteDeleteAsync();
        await db.Leads.ExecuteDeleteAsync();
        await db.Corretores.ExecuteDeleteAsync();
        var supervisor = NovaConta("Silvia Souza", PerfisDoPainel.Supervisor, aprovado: false);
        var corretor = NovaConta("Helena Braga");
        var outro = NovaConta("Carlos Dias");
        var pendente = NovaConta("Pedro Pendente", aprovado: false);
        var cliente = NovaConta("Cliente Conta", PerfisDoPainel.Cliente);
        var outroPendente = NovaConta("Outro Pendente", aprovado: false);
        var inativo = NovaConta("Inativo Pendente", aprovado: false);
        inativo.Desativar();
        db.Corretores.AddRange(supervisor, corretor, outro, pendente, cliente, outroPendente, inativo);
        db.Entry(supervisor).Property(c => c.StatusCorretor).CurrentValue = StatusDoCorretor.EmAnalise;
        db.Entry(cliente).Property(c => c.StatusCorretor).CurrentValue = StatusDoCorretor.EmAnalise;
        var supervisorToken = Sessao(db, supervisor);
        var corretorToken = Sessao(db, corretor);
        var pendenteToken = Sessao(db, pendente);
        var clienteToken = Sessao(db, cliente);
        await db.SaveChangesAsync();
        return new(supervisorToken, corretorToken, pendenteToken, clienteToken, corretor, outro, pendente);
    }

    private async Task<Cenario> CriarCenarioCompletoAsync()
    {
        var cenario = await CriarCenarioAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        int?[] scores = [0, 39, 40, 69, 70, 100, null];
        string?[] intencoes = [Intencoes.Compra, Intencoes.Compra, Intencoes.Aluguel,
            Intencoes.Aluguel, Intencoes.Investimento, null, Intencoes.Indefinida];
        string[] regioes = ["Moema", "MOÉMA", "Chácara Klabin", "CHACARA KLABIN", "Alfa", "Beta", "Gama"];
        var leads = Enumerable.Range(0, 7).Select(i => NovoLead(
            db, Agora.AddDays(i == 5 ? -32 : -20), intencoes[i], scores[i], regioes[i])).ToArray();
        foreach (var lead in leads.Take(3))
        {
            lead.RegistrarConsentimento("s22", Agora);
        }

        var propria = NovaConversa(db, leads[0], Agora.AddDays(-50), Agora.AddDays(-5),
            cenario.Corretor.Id, imovelId: "IMV-1", bairro: "Moema");
        Resposta(db, propria.Id, Agora.AddDays(-5).AddMinutes(1), true, "IMV-1", "Moema", repetir: true);
        Resposta(db, propria.Id, Agora.AddDays(-5).AddMinutes(2), true, "IMV-1", "Moema");
        var alheia = NovaConversa(db, leads[0], Agora.AddDays(-4), Agora.AddDays(-4),
            cenario.Outro.Id, imovelId: "IMV-1", bairro: "Moema");
        Resposta(db, alheia.Id, Agora.AddDays(-4), true, "IMV-2", "Vila Nova");
        NovaConversa(db, leads[0], Agora.AddDays(-3), Agora.AddDays(-3), null);
        var limite = NovaConversa(db, leads[1], Agora.AddDays(-30), Agora.AddDays(-30),
            cenario.Corretor.Id, imovelId: "IMV-3", bairro: "Centro");
        db.Mensagens.Add(Mensagem.DoLead(limite.Id, TextoCanario, Agora.AddDays(-1)));
        Resposta(db, limite.Id, Agora.AddDays(-1), true);
        var antiga = NovaConversa(db, leads[2], Agora.AddDays(-31), Agora.AddDays(-31),
            cenario.Outro.Id, imovelId: "IMV-ANTIGO", bairro: "Tatuapé");
        db.Mensagens.Add(Mensagem.DoLead(antiga.Id, TextoCanario, Agora.AddDays(-1)));
        Resposta(db, antiga.Id, Agora.AddDays(-1), true);
        var soOla = NovaConversa(db, leads[3], Agora.AddDays(-10), null, null, false);
        Resposta(db, soOla.Id, Agora.AddDays(-1), true);
        NovaConversa(db, leads[4], Agora, Agora.AddDays(1), null);
        NovaConversa(db, leads[6], Agora, Agora, null);
        var propriaEnc = db.Encaminhamentos.Local.Single(e => e.ConversaId == propria.Id);
        var alheiaEnc = db.Encaminhamentos.Local.Single(e => e.ConversaId == alheia.Id);
        db.Entry(propriaEnc).Property(e => e.Id).CurrentValue = 900002;
        db.Entry(alheiaEnc).Property(e => e.Id).CurrentValue = 900001;
        propriaEnc.Atribuir(cenario.Corretor.Id, Agora.AddDays(-1));
        alheiaEnc.Atribuir(cenario.Outro.Id, Agora.AddDays(-1));

        cenario.UltimoEncaminhamentoId = propriaEnc.Id;
        AdicionarSlot(db, cenario.Corretor.Id, leads[0].Id, Agora);
        AdicionarSlot(db, cenario.Corretor.Id, leads[0].Id, Agora.AddDays(7));
        AdicionarSlot(db, cenario.Corretor.Id, leads[0].Id, Agora.AddDays(7).AddMilliseconds(1));
        AdicionarSlot(db, cenario.Outro.Id, leads[0].Id, Agora.AddDays(2));
        AdicionarSlot(db, cenario.Corretor.Id, leads[2].Id, Agora.AddDays(3));
        AdicionarSlot(db, cenario.Corretor.Id, null, Agora.AddDays(4));
        AdicionarSlot(db, cenario.Corretor.Id, leads[0].Id, Agora.AddSeconds(-1));
        AdicionarSlot(db, cenario.Outro.Id, leads[2].Id, Agora.AddDays(9));
        await db.SaveChangesAsync();
        return cenario;
    }

    private static Lead NovoLead(
        SolarDbContext db, DateTimeOffset criadoEm, string? intencao, int? score, string? regiao)
    {
        var lead = Lead.Novo(criadoEm);
        lead.Fundir(intencao ?? Intencoes.Indefinida, new CamposExtraidos(Score: score, Regiao: regiao), criadoEm);
        lead.RegistrarContato(NomeCanario, TelefoneCanario, EmailCanario, criadoEm);
        db.Leads.Add(lead);
        if (intencao == Intencoes.Indefinida)
        {
            db.Entry(lead).Property(l => l.Intencao).CurrentValue = Intencoes.Indefinida;
        }

        return lead;
    }

    private static Conversa NovaConversa(SolarDbContext db, Lead lead, DateTimeOffset criadaEm,
        DateTimeOffset? primeiraMensagem, Guid? corretorId, bool encaminhada = true,
        string? imovelId = null, string bairro = "Centro")
    {
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, criadaEm);
        conversa.ReapontarLead(lead);
        db.Conversas.Add(conversa);
        db.Mensagens.Add(Mensagem.DaLia(conversa.Id, "Olá automático", ProximasAcoes.ContinuarConversa, criadaEm));
        if (primeiraMensagem is { } em)
        {
            db.Mensagens.Add(Mensagem.DoLead(conversa.Id, TextoCanario, em));
            Resposta(db, conversa.Id, em, false, imovelId, bairro);
        }

        if (encaminhada)
        {
            db.Encaminhamentos.Add(Encaminhamento.Novo(
                conversa.Id, lead.Id, corretorId, Especialidades.Moradia, criadaEm));
        }

        return conversa;
    }

    private static void Resposta(SolarDbContext db, Guid conversaId, DateTimeOffset em,
        bool confirmado, string? imovelId = null, string bairro = "Centro", bool repetir = false)
    {
        var imovel = imovelId is null ? null : new ImovelSugerido(
            imovelId, "apartamento", bairro, 2, 60, 500_000, null, TextoCanario);
        var mensagem = Mensagem.DaLia(conversaId, TextoCanario, ProximasAcoes.SugerirImoveis,
            em, imovel is null ? [] : repetir ? [imovel, imovel] : [imovel]);
        if (confirmado)
        {
            mensagem.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
        }

        db.Mensagens.Add(mensagem);
    }

    private static void AdicionarSlot(SolarDbContext db, Guid corretorId, Guid? leadId, DateTimeOffset inicio)
    {
        var slot = Slot.Novo(corretorId, inicio, inicio.AddHours(1));
        db.Slots.Add(slot);
        db.Entry(slot).Property(s => s.LeadId).CurrentValue = leadId;
    }

    private static Corretor NovaConta(
        string nome, string perfil = PerfisDoPainel.Corretor, bool aprovado = true)
    {
        var email = Guid.NewGuid().ToString("N") + "@tests.solar.local";
        var conta = Corretor.NovaConta(nome, email, email.ToUpperInvariant(), "11987654321",
            "hash-s22", perfil, [], [], "s22", Agora.AddDays(-1));
        if (aprovado && perfil != PerfisDoPainel.Cliente)
        {
            conta.Aprovar(Agora);
        }

        return conta;
    }

    private static string Sessao(SolarDbContext db, Corretor conta)
    {
        var token = TokenSeguro.Criar();
        db.Sessoes.Add(SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), Agora));
        return token;
    }

    private HttpClient Cliente(string? token)
    {
        var client = fixture.Factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", CorretorAuthenticationDefaults.CookieName + "=" + token);
        }

        return client;
    }

    private async Task<MetricasPainelResponse> ObterAsync(string token)
    {
        using var client = Cliente(token);
        using var resposta = await client.GetAsync("/api/painel/metricas?dias=30");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>())!;
    }

    private static void AssertZeradas(MetricasPainelResponse metricas)
    {
        Assert.Equal(0, metricas.ConversasIniciadas);
        Assert.Equal(0, metricas.HorariosConfirmados);
        Assert.Equal(0, metricas.ReservasProximos7Dias);
        Assert.Equal(new IntencoesMetricasPainel(0, 0, 0, 0), metricas.LeadsPorIntencao);
        Assert.Equal(new ScoreMetricasPainel(0, 0, 0, 0), metricas.Extras.Score);
        Assert.Empty(metricas.Extras.Regioes.Top);
        Assert.Equal(0, metricas.Extras.Regioes.Outras);
        Assert.Equal(0, metricas.Extras.Regioes.Informaram);
        Assert.Equal(0, metricas.Extras.Regioes.Leads);
        Assert.Empty(metricas.Extras.Imoveis);
        Assert.Empty(metricas.Extras.ProximosHorarios);
        Assert.Equal(0, metricas.Extras.Privacidade.Leads);
        Assert.Equal(0, metricas.Extras.Privacidade.ComConsentimento);
        Assert.Equal(0, metricas.Extras.Privacidade.Vencem30Dias);
        Assert.Null(metricas.Extras.Privacidade.ProximoVencimento);
        Assert.Null(metricas.Extras.TempoMedianoMin);
        Assert.Empty(metricas.Extras.TempoMedianoDiario);
        Assert.Equal(new FollowUpMetricasPainel(metricas.Extras.FollowUp.JanelaDias, 0, 0, 0, 0), metricas.Extras.FollowUp);
        Assert.NotNull(metricas.Avanco);
        Assert.All(metricas.Avanco, a => Assert.Equal(0, a.Conversas));
    }

    private sealed record Cenario(
        string SupervisorToken, string CorretorToken, string PendenteToken, string ClienteToken,
        Corretor Corretor, Corretor Outro, Corretor Pendente)
    {
        public long UltimoEncaminhamentoId { get; set; }
    }
}
