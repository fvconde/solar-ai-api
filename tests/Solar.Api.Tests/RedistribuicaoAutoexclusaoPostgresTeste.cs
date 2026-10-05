using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class RedistribuicaoAutoexclusaoPostgresTeste : IAsyncLifetime
{
    private readonly MetricasPainelPostgresFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    private async Task DesativarCorretoresSeedAsync()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var corretores = await db.Corretores.ToListAsync();
        foreach (var c in corretores)
        {
            c.Desativar();
        }
        await db.SaveChangesAsync();
    }

    private static Corretor CriarCorretor(
        string nome,
        string regiao,
        DateTimeOffset criadoEm,
        string especialidade = Especialidades.Moradia)
    {
        var email = $"{Guid.NewGuid():N}@tests.solar.local";
        var corretor = Corretor.NovaConta(
            nome: nome,
            email: email,
            emailNormalizado: email.ToLowerInvariant(),
            telefone: "11987654321",
            senhaHash: "hash-teste",
            perfil: PerfisDoPainel.Corretor,
            regioes: [regiao],
            especialidades: [especialidade],
            versaoAvisoPrivacidade: AvisoPrivacidade.VersaoAtual,
            em: criadoEm);
        corretor.Aprovar(criadoEm);
        return corretor;
    }

    private static Corretor CriarSupervisor(DateTimeOffset em)
    {
        var email = $"supervisor-{Guid.NewGuid():N}@tests.solar.local";
        var supervisor = Corretor.NovaConta(
            nome: "Supervisor S46",
            email: email,
            emailNormalizado: email.ToLowerInvariant(),
            telefone: "11999990000",
            senhaHash: "hash-teste",
            perfil: PerfisDoPainel.Supervisor,
            regioes: [],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: AvisoPrivacidade.VersaoAtual,
            em: em);
        supervisor.Aprovar(em);
        return supervisor;
    }

    private async Task<string> CriarSessaoAsync(Corretor conta)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        var token = TokenSeguro.Criar();
        db.Sessoes.Add(SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), _fixture.Relogio.GetUtcNow()));
        await db.SaveChangesAsync();
        return token;
    }

    private HttpClient CriarClienteHttp(string? token = null)
    {
        var client = _fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={token}");
        }
        return client;
    }

    private async Task<MetricasPainelResponse> ObterMetricasSupervisorAsync(string tokenSupervisor, int dias = 30)
    {
        using var client = CriarClienteHttp(tokenSupervisor);
        using var resposta = await client.GetAsync($"/api/painel/metricas?dias={dias}");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<MetricasPainelResponse>())!;
    }

    [Fact]
    public async Task Redistribuir_nao_reescolhe_corretor_de_origem_ativo()
    {
        await DesativarCorretoresSeedAsync();

        var agora = MetricasPainelPostgresFixture.Agora;
        var tCriadoA = agora.AddDays(-20);
        var tCriadoB = agora.AddDays(-10);
        var tConversa = agora.AddDays(-5);
        var regiao = $"regiao{Guid.NewGuid():N}";

        var corretorA = CriarCorretor("Corretor A", regiao, tCriadoA);
        var corretorB = CriarCorretor("Corretor B", regiao, tCriadoB);

        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, tConversa);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tConversa);
        conversa.RegistrarTurno(
            "Mensagem inicial",
            new TurnoResponse(
                Resposta: "Atendimento iniciado",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tConversa);
        conversa.RegistrarCorretorAtribuido(tConversa);

        var encaminhamento = Encaminhamento.Novo(
            conversa.Id,
            conversa.LeadId,
            corretorA.Id,
            Especialidades.Moradia,
            tConversa);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.AddRange(corretorA, corretorB);
            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
        }

        await using (var scopeExec = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbExec = scopeExec.ServiceProvider.GetRequiredService<SolarDbContext>();
            var repo = new EncaminhamentoRepositorio(dbExec);
            await repo.RedistribuirAsync(corretorA.Id, agora, CancellationToken.None);
        }

        await using (var scopeVerifica = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbVerifica = scopeVerifica.ServiceProvider.GetRequiredService<SolarDbContext>();
            var encaminhamentoAtualizado = await dbVerifica.Encaminhamentos
                .AsNoTracking()
                .SingleAsync(e => e.Id == encaminhamento.Id);

            Assert.Equal(corretorB.Id, encaminhamentoAtualizado.CorretorId);
            Assert.NotEqual(corretorA.Id, encaminhamentoAtualizado.CorretorId);
            Assert.Equal(StatusDoEncaminhamento.Atribuido, encaminhamentoAtualizado.Status);
        }
    }

    [Fact]
    public async Task Exclusao_http_com_dois_elegiveis_transfere_para_B_e_preserva_avanco()
    {
        await DesativarCorretoresSeedAsync();

        var agora = MetricasPainelPostgresFixture.Agora;
        var tCriadoA = agora.AddDays(-20);
        var tCriadoB = agora.AddDays(-10);
        var tSupervisor = agora.AddDays(-25);
        var tConversa = agora.AddDays(-5);
        var tTurno = agora.AddDays(-4);
        var tFollowUp = agora.AddDays(-3);
        var regiao = $"regiao{Guid.NewGuid():N}";

        var supervisor = CriarSupervisor(tSupervisor);
        var corretorA = CriarCorretor("Corretor A", regiao, tCriadoA);
        var corretorB = CriarCorretor("Corretor B", regiao, tCriadoB);

        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, tConversa);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tConversa);
        conversa.RegistrarTurno(
            "Gostaria de agendar visita",
            new TurnoResponse(
                Resposta: "Com certeza, temos opcoes.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tTurno);
        conversa.RegistrarCorretorAtribuido(tTurno);
        conversa.RegistrarFollowUp(
            new TurnoResponse(
                Resposta: "Como posso ajudar mais?",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tFollowUp);

        var encaminhamento = Encaminhamento.Novo(
            conversa.Id,
            conversa.LeadId,
            corretorA.Id,
            Especialidades.Moradia,
            tTurno);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.AddRange(supervisor, corretorA, corretorB);
            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
        }

        var tokenSupervisor = await CriarSessaoAsync(supervisor);
        var tokenA = await CriarSessaoAsync(corretorA);

        Conversa conversaAntes;
        await using (var scopeAntes = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbAntes = scopeAntes.ServiceProvider.GetRequiredService<SolarDbContext>();
            conversaAntes = await dbAntes.Conversas
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversa.Id);
        }

        Assert.NotNull(conversaAntes.CorretorAtribuidoEm);
        Assert.NotNull(conversaAntes.IntencaoEm);
        Assert.NotNull(conversaAntes.EssenciaisEm);
        Assert.NotNull(conversaAntes.EncaminhadaEm);
        Assert.NotNull(conversaAntes.PrimeiroReengajamentoEm);

        var metricasAntes = await ObterMetricasSupervisorAsync(tokenSupervisor, 30);
        Assert.NotNull(metricasAntes.Avanco);
        Assert.NotEmpty(metricasAntes.Avanco);

        using var clientA = CriarClienteHttp(tokenA);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/conta")
        {
            Content = JsonContent.Create(new ExcluirContaRequest(corretorA.Email)),
        };
        using var resposta = await clientA.SendAsync(request);
        var corpo = await resposta.Content.ReadAsStringAsync();

        if (resposta.StatusCode != HttpStatusCode.NoContent)
        {
            await using var dbRollbackScope = _fixture.Factory.Services.CreateAsyncScope();
            var dbRollback = dbRollbackScope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var contaExiste = await dbRollback.Corretores.AnyAsync(c => c.Id == corretorA.Id);
            var encaminhamentoComA = await dbRollback.Encaminhamentos.AnyAsync(e => e.Id == encaminhamento.Id && e.CorretorId == corretorA.Id);
            var conversaIntacta = await dbRollback.Conversas.AnyAsync(c => c.Id == conversa.Id && c.CorretorAtribuidoEm == conversaAntes.CorretorAtribuidoEm);
            Assert.True(contaExiste, $"Falha HTTP deve sofrer rollback e manter a conta no banco. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
            Assert.True(encaminhamentoComA, $"Falha HTTP deve sofrer rollback e manter o encaminhamento associado a A. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
            Assert.True(conversaIntacta, $"Falha HTTP deve sofrer rollback e manter os marcos da conversa. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
        }

        Assert.True(
            resposta.StatusCode == HttpStatusCode.NoContent,
            $"Esperava 204 NoContent, mas recebeu {(int)resposta.StatusCode} ({resposta.StatusCode}): {corpo}");

        await using (var scopeDepois = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbDepois = scopeDepois.ServiceProvider.GetRequiredService<SolarDbContext>();

            Assert.False(await dbDepois.Corretores.AnyAsync(c => c.Id == corretorA.Id));
            Assert.True(await dbDepois.Corretores.AnyAsync(c => c.Id == corretorB.Id && c.Ativo));

            var encDepois = await dbDepois.Encaminhamentos.SingleAsync(e => e.Id == encaminhamento.Id);
            Assert.Equal(corretorB.Id, encDepois.CorretorId);
            Assert.Equal(StatusDoEncaminhamento.Atribuido, encDepois.Status);

            var conversaDepois = await dbDepois.Conversas.SingleAsync(c => c.Id == conversa.Id);
            Assert.Equal(conversaAntes.CorretorAtribuidoEm, conversaDepois.CorretorAtribuidoEm);
            Assert.Equal(conversaAntes.IntencaoEm, conversaDepois.IntencaoEm);
            Assert.Equal(conversaAntes.EssenciaisEm, conversaDepois.EssenciaisEm);
            Assert.Equal(conversaAntes.EncaminhadaEm, conversaDepois.EncaminhadaEm);
            Assert.Equal(conversaAntes.PrimeiroReengajamentoEm, conversaDepois.PrimeiroReengajamentoEm);
            Assert.Equal(conversaAntes.CriadaEm, conversaDepois.CriadaEm);
        }

        var metricasDepois = await ObterMetricasSupervisorAsync(tokenSupervisor, 30);
        Assert.Equal(metricasAntes.Avanco.Count, metricasDepois.Avanco.Count);
        for (var i = 0; i < metricasAntes.Avanco.Count; i++)
        {
            Assert.Equal(metricasAntes.Avanco[i].Etapa, metricasDepois.Avanco[i].Etapa);
            Assert.Equal(metricasAntes.Avanco[i].Conversas, metricasDepois.Avanco[i].Conversas);
            Assert.Equal(metricasAntes.Avanco[i].SemEssenciais, metricasDepois.Avanco[i].SemEssenciais);
        }
    }

    [Fact]
    public async Task Exclusao_http_do_unico_elegivel_deixa_aguardando_e_preserva_avanco()
    {
        await DesativarCorretoresSeedAsync();

        var agora = MetricasPainelPostgresFixture.Agora;
        var tCriadoA = agora.AddDays(-20);
        var tSupervisor = agora.AddDays(-25);
        var tConversa = agora.AddDays(-5);
        var tTurno = agora.AddDays(-4);
        var tFollowUp = agora.AddDays(-3);
        var regiao = $"regiao{Guid.NewGuid():N}";

        var supervisor = CriarSupervisor(tSupervisor);
        var corretorA = CriarCorretor("Corretor A", regiao, tCriadoA);

        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, tConversa);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tConversa);
        conversa.RegistrarTurno(
            "Gostaria de agendar visita",
            new TurnoResponse(
                Resposta: "Com certeza, temos opcoes.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tTurno);
        conversa.RegistrarCorretorAtribuido(tTurno);
        conversa.RegistrarFollowUp(
            new TurnoResponse(
                Resposta: "Como posso ajudar mais?",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tFollowUp);

        var encaminhamento = Encaminhamento.Novo(
            conversa.Id,
            conversa.LeadId,
            corretorA.Id,
            Especialidades.Moradia,
            tTurno);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.AddRange(supervisor, corretorA);
            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
        }

        var tokenSupervisor = await CriarSessaoAsync(supervisor);
        var tokenA = await CriarSessaoAsync(corretorA);

        Conversa conversaAntes;
        await using (var scopeAntes = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbAntes = scopeAntes.ServiceProvider.GetRequiredService<SolarDbContext>();
            conversaAntes = await dbAntes.Conversas
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversa.Id);
        }

        Assert.NotNull(conversaAntes.CorretorAtribuidoEm);
        Assert.NotNull(conversaAntes.IntencaoEm);
        Assert.NotNull(conversaAntes.EssenciaisEm);
        Assert.NotNull(conversaAntes.EncaminhadaEm);
        Assert.NotNull(conversaAntes.PrimeiroReengajamentoEm);

        var metricasAntes = await ObterMetricasSupervisorAsync(tokenSupervisor, 30);
        Assert.NotNull(metricasAntes.Avanco);
        Assert.NotEmpty(metricasAntes.Avanco);

        using var clientA = CriarClienteHttp(tokenA);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/conta")
        {
            Content = JsonContent.Create(new ExcluirContaRequest(corretorA.Email)),
        };
        using var resposta = await clientA.SendAsync(request);
        var corpo = await resposta.Content.ReadAsStringAsync();

        if (resposta.StatusCode != HttpStatusCode.NoContent)
        {
            await using var dbRollbackScope = _fixture.Factory.Services.CreateAsyncScope();
            var dbRollback = dbRollbackScope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var contaExiste = await dbRollback.Corretores.AnyAsync(c => c.Id == corretorA.Id);
            var encaminhamentoComA = await dbRollback.Encaminhamentos.AnyAsync(e => e.Id == encaminhamento.Id && e.CorretorId == corretorA.Id);
            var conversaIntacta = await dbRollback.Conversas.AnyAsync(c => c.Id == conversa.Id && c.CorretorAtribuidoEm == conversaAntes.CorretorAtribuidoEm);
            Assert.True(contaExiste, $"Falha HTTP deve sofrer rollback e manter a conta no banco. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
            Assert.True(encaminhamentoComA, $"Falha HTTP deve sofrer rollback e manter o encaminhamento associado a A. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
            Assert.True(conversaIntacta, $"Falha HTTP deve sofrer rollback e manter os marcos da conversa. Status: {(int)resposta.StatusCode}, Resposta: {corpo}");
        }

        Assert.True(
            resposta.StatusCode == HttpStatusCode.NoContent,
            $"Esperava 204 NoContent, mas recebeu {(int)resposta.StatusCode} ({resposta.StatusCode}): {corpo}");

        await using (var scopeDepois = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbDepois = scopeDepois.ServiceProvider.GetRequiredService<SolarDbContext>();

            Assert.False(await dbDepois.Corretores.AnyAsync(c => c.Id == corretorA.Id));

            var encDepois = await dbDepois.Encaminhamentos.SingleAsync(e => e.Id == encaminhamento.Id);
            Assert.Null(encDepois.CorretorId);
            Assert.Equal(StatusDoEncaminhamento.Aguardando, encDepois.Status);

            var conversaDepois = await dbDepois.Conversas.SingleAsync(c => c.Id == conversa.Id);
            Assert.Equal(conversaAntes.CorretorAtribuidoEm, conversaDepois.CorretorAtribuidoEm);
            Assert.Equal(conversaAntes.IntencaoEm, conversaDepois.IntencaoEm);
            Assert.Equal(conversaAntes.EssenciaisEm, conversaDepois.EssenciaisEm);
            Assert.Equal(conversaAntes.EncaminhadaEm, conversaDepois.EncaminhadaEm);
            Assert.Equal(conversaAntes.PrimeiroReengajamentoEm, conversaDepois.PrimeiroReengajamentoEm);
            Assert.Equal(conversaAntes.CriadaEm, conversaDepois.CriadaEm);
        }

        var metricasDepois = await ObterMetricasSupervisorAsync(tokenSupervisor, 30);
        Assert.Equal(metricasAntes.Avanco.Count, metricasDepois.Avanco.Count);
        for (var i = 0; i < metricasAntes.Avanco.Count; i++)
        {
            Assert.Equal(metricasAntes.Avanco[i].Etapa, metricasDepois.Avanco[i].Etapa);
            Assert.Equal(metricasAntes.Avanco[i].Conversas, metricasDepois.Avanco[i].Conversas);
            Assert.Equal(metricasAntes.Avanco[i].SemEssenciais, metricasDepois.Avanco[i].SemEssenciais);
        }
    }

    [Fact]
    public async Task Supervisor_recusa_corretor_com_encaminhamento_redistribui_sem_regressao()
    {
        await DesativarCorretoresSeedAsync();

        var agora = MetricasPainelPostgresFixture.Agora;
        var tCriadoA = agora.AddDays(-20);
        var tCriadoB = agora.AddDays(-10);
        var tSupervisor = agora.AddDays(-25);
        var tConversa = agora.AddDays(-5);
        var tTurno = agora.AddDays(-4);
        var regiao = $"regiao{Guid.NewGuid():N}";

        var supervisor = CriarSupervisor(tSupervisor);

        var emailA = $"corretor-recusa-{Guid.NewGuid():N}@tests.solar.local";
        var corretorA = Corretor.NovaConta(
            nome: "Corretor A Em Analise",
            email: emailA,
            emailNormalizado: emailA.ToLowerInvariant(),
            telefone: "11987654321",
            senhaHash: "hash-teste",
            perfil: PerfisDoPainel.Corretor,
            regioes: [regiao],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: AvisoPrivacidade.VersaoAtual,
            em: tCriadoA);

        var corretorB = CriarCorretor("Corretor B", regiao, tCriadoB);

        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, tConversa);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tConversa);
        conversa.RegistrarTurno(
            "Gostaria de atendimento",
            new TurnoResponse(
                Resposta: "Atendimento iniciado",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: regiao),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true),
            tTurno);
        conversa.RegistrarCorretorAtribuido(tTurno);
        var marcoAtribuidoAntes = conversa.CorretorAtribuidoEm;
        Assert.NotNull(marcoAtribuidoAntes);

        var encaminhamento = Encaminhamento.Novo(
            conversa.Id,
            conversa.LeadId,
            corretorA.Id,
            Especialidades.Moradia,
            tTurno);

        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.AddRange(supervisor, corretorA, corretorB);
            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
        }

        await using (var scopeAntes = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbAntes = scopeAntes.ServiceProvider.GetRequiredService<SolarDbContext>();
            var corretorABanco = await dbAntes.Corretores.SingleAsync(c => c.Id == corretorA.Id);
            Assert.Equal(StatusDoCorretor.EmAnalise, corretorABanco.StatusCorretor);
            Assert.True(corretorABanco.Ativo);
        }

        var tokenSupervisor = await CriarSessaoAsync(supervisor);
        var tokenA = await CriarSessaoAsync(corretorA);

        var fakeEmail = new EnviadorFake();
        using var host = _fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnviadorEmail>();
            services.AddSingleton<IEnviadorEmail>(fakeEmail);
        }));

        using var clientSupervisor = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        clientSupervisor.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={tokenSupervisor}");

        using var clientA = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        clientA.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={tokenA}");

        const string motivo = "Documentação incompleta";
        using var resposta = await clientSupervisor.PostAsJsonAsync(
            $"/api/painel/corretores/{corretorA.Id:D}/recusa",
            new { motivo });
        Assert.Equal(HttpStatusCode.NoContent, resposta.StatusCode);

        using var sessaoA = await clientA.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.Unauthorized, sessaoA.StatusCode);

        await using (var scopeDepois = _fixture.Factory.Services.CreateAsyncScope())
        {
            var dbDepois = scopeDepois.ServiceProvider.GetRequiredService<SolarDbContext>();

            Assert.False(await dbDepois.Corretores.AnyAsync(c => c.Id == corretorA.Id));
            Assert.False(await dbDepois.Sessoes.AnyAsync(s => s.CorretorId == corretorA.Id));

            var encDepois = await dbDepois.Encaminhamentos.SingleAsync(e => e.Id == encaminhamento.Id);
            Assert.Equal(corretorB.Id, encDepois.CorretorId);
            Assert.Equal(StatusDoEncaminhamento.Atribuido, encDepois.Status);

            var conversaDepois = await dbDepois.Conversas.SingleAsync(c => c.Id == conversa.Id);
            Assert.Equal(marcoAtribuidoAntes, conversaDepois.CorretorAtribuidoEm);
        }

        var recusa = Assert.Single(fakeEmail.Recusas);
        Assert.Equal(corretorA.Id, recusa.ContaId);
        Assert.Equal(corretorA.Email, recusa.Email);
        Assert.Equal(corretorA.Nome, recusa.Nome);
        Assert.Equal(motivo, recusa.Motivo);
    }

    private sealed record MensagemRecusaEmail(Guid ContaId, string Email, string Nome, string? Motivo = null);

    private sealed class EnviadorFake : IEnviadorEmail
    {
        public List<MensagemRecusaEmail> Recusas { get; } = [];

        public Task EnviarLinkRecuperacaoAsync(
            string destinatario,
            string link,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnviarAprovacaoAsync(
            Guid contaId,
            string destinatario,
            string nome,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnviarRecusaAsync(
            Guid contaId,
            string destinatario,
            string nome,
            string? motivo,
            CancellationToken cancellationToken = default)
        {
            Recusas.Add(new MensagemRecusaEmail(contaId, destinatario, nome, motivo));
            return Task.CompletedTask;
        }
    }
}
