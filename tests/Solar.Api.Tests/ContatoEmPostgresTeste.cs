using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Solar.Api.Agendamentos;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class ContatoEmPostgresTeste
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Information_schema_confirma_coluna_contato_em_na_tabela_leads()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != System.Data.ConnectionState.Open)
        {
            await conexao.OpenAsync();
        }

        const string sql = """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_name = 'leads'
              AND column_name = 'contato_em';
            """;

        await using var cmd = new NpgsqlCommand(sql, conexao);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("contato_em", reader.GetString(0));
        Assert.Equal("timestamp with time zone", reader.GetString(1));
        Assert.Equal("YES", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public async Task Post_contato_devolve_contatoEm_nao_nulo_ISO8601_e_persiste_no_lead_e_Get_devolve_timestamp()
    {
        var conversaId = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        Guid leadId;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var req = new ContatoRequest("Ana Paula", "11988887777", "ana.paula@teste.local");
            var resultadoPost = await controller.RegistrarContato(conversaId, req, default);
            var okPost = Assert.IsType<OkObjectResult>(resultadoPost.Result);
            var respostaPost = Assert.IsType<ContatoResponse>(okPost.Value);

            Assert.NotNull(respostaPost.ContatoEm);
            var jsonPost = JsonSerializer.Serialize(respostaPost, JsonWeb);
            Assert.Contains("\"contatoEm\":", jsonPost);
            Assert.DoesNotContain("\"contatoEm\":null", jsonPost);

            await using var dbLeitura = await PostgresTestDatabase.CriarContextoAsync();
            var leadPersistido = await dbLeitura.Leads.AsNoTracking().SingleAsync(l => l.Id == respostaPost.LeadId);
            Assert.NotNull(leadPersistido.ContatoEm);

            var diferencaMs = Math.Abs((leadPersistido.ContatoEm.Value - respostaPost.ContatoEm.Value).TotalMilliseconds);
            Assert.True(diferencaMs < 1, $"Diferenca entre memória e banco excede precisão esperada: {diferencaMs}ms");
            Assert.Equal(
                leadPersistido.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                respostaPost.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"));

            var resultadoGet = await controller.Obter(conversaId, default);
            var okGet = Assert.IsType<OkObjectResult>(resultadoGet.Result);
            var respostaGet = Assert.IsType<ConversaResponse>(okGet.Value);

            Assert.NotNull(respostaGet.ContatoEm);
            Assert.Equal(leadPersistido.ContatoEm.Value, respostaGet.ContatoEm.Value);
            Assert.Equal(
                respostaPost.ContatoEm.Value.ToString("HH:mm"),
                respostaGet.ContatoEm.Value.ToString("HH:mm"));
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Get_conversa_sem_contato_contem_contatoEm_nulo()
    {
        var conversaId = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        Guid leadId;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var leadPersistido = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
            Assert.Null(leadPersistido.ContatoEm);

            var controller = CriarController(db);
            var resultadoGet = await controller.Obter(conversaId, default);
            var okGet = Assert.IsType<OkObjectResult>(resultadoGet.Result);
            var respostaGet = Assert.IsType<ConversaResponse>(okGet.Value);

            Assert.Null(respostaGet.ContatoEm);

            var jsonGet = JsonSerializer.Serialize(respostaGet, JsonWeb);
            Assert.Contains("\"contatoEm\":null", jsonGet);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Dedupe_por_telefone_e_email_usa_lead_canonico_e_sua_data_gravada_sem_devolver_data_de_orfao_e_preserva_slots()
    {
        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        var dataInicialCanonico = agora.AddHours(-3);
        Guid canonicoId;
        Guid orfaoId;
        Corretor? corretor = null;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(preparacao);

            var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, dataInicialCanonico, default);
            canonicoId = conversa1.LeadId;
            conversa1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, dataInicialCanonico);
            conversa1.Lead.RegistrarContato("Cliente Canonico", "11977778888", "canonico@teste.local", dataInicialCanonico);

            var conversa2 = await repo.ObterOuCriarAsync(conversa2Id, agora, default);
            orfaoId = conversa2.LeadId;
            conversa2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);

            corretor = CriarCorretor(agora);
            preparacao.Corretores.Add(corretor);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa2.Id, orfaoId, corretor.Id, corretor.Especialidade, agora));

            preparacao.Slots.AddRange(
                Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1)),
                Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1)));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var req = new ContatoRequest("Nome Atualizado", "11977778888", "canonico@teste.local");
            var resultadoPost = await controller.RegistrarContato(conversa2Id, req, default);
            var okPost = Assert.IsType<OkObjectResult>(resultadoPost.Result);
            var respostaPost = Assert.IsType<ContatoResponse>(okPost.Value);

            Assert.Equal(canonicoId, respostaPost.LeadId);
            Assert.NotEqual(orfaoId, respostaPost.LeadId);
            Assert.NotNull(respostaPost.ContatoEm);
            Assert.True(respostaPost.ContatoEm.Value > dataInicialCanonico);

            Assert.NotEmpty(respostaPost.Oferta);
            Assert.Equal(2, respostaPost.Oferta.Count);

            await using var dbVerificacao = await PostgresTestDatabase.CriarContextoAsync();
            Assert.False(await dbVerificacao.Leads.AnyAsync(l => l.Id == orfaoId));

            var leadCanonicoBanco = await dbVerificacao.Leads.AsNoTracking().SingleAsync(l => l.Id == canonicoId);
            Assert.NotNull(leadCanonicoBanco.ContatoEm);
            Assert.Equal(
                leadCanonicoBanco.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                respostaPost.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"));

            var conversa2Banco = await dbVerificacao.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversa2Id);
            Assert.Equal(canonicoId, conversa2Banco.LeadId);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            if (corretor != null)
            {
                await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
                await limpeza.Encaminhamentos.Where(e => e.CorretorId == corretor.Id).ExecuteDeleteAsync();
                await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
            }
            await limpeza.Conversas.Where(c => c.Id == conversa1Id || c.Id == conversa2Id).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == canonicoId || l.Id == orfaoId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Exclusao_do_titular_e_lead_remove_lead_com_seu_contatoEm_e_preserva_lead_independente()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversaAId = Guid.NewGuid();
        var conversaA = await repo.ObterOuCriarAsync(conversaAId, agora, default);
        var leadA = conversaA.Lead;
        var dataContatoA = agora.AddHours(-1);
        leadA.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
        leadA.RegistrarContato("Titular A", "11911110001", "titular.a@teste.local", dataContatoA);

        var conversaBId = Guid.NewGuid();
        var conversaB = await repo.ObterOuCriarAsync(conversaBId, agora, default);
        var leadB = conversaB.Lead;
        var dataContatoB = agora.AddHours(-2);
        leadB.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
        leadB.RegistrarContato("Titular B", "11922220002", "titular.b@teste.local", dataContatoB);

        await db.SaveChangesAsync();

        var leadAId = leadA.Id;
        var leadBId = leadB.Id;

        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadAId && l.ContatoEm != null));
        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadBId && l.ContatoEm != null));

        var resultadoExclusao = await repo.ExcluirLeadAsync(leadAId, default);
        Assert.NotNull(resultadoExclusao);

        Assert.False(await db.Leads.AnyAsync(l => l.Id == leadAId));
        Assert.Equal(0, await db.Conversas.CountAsync(c => c.LeadId == leadAId));

        var leadBPersistido = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadBId);
        Assert.NotNull(leadBPersistido.ContatoEm);
        Assert.Equal(
            dataContatoB.ToString("yyyy-MM-ddTHH:mm:ss"),
            leadBPersistido.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"));

        await repo.ExcluirLeadAsync(leadBId, default);
    }

    [Fact]
    public async Task Exclusao_apenas_conversa_preserva_lead_compartilhado_com_seu_contatoEm()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();

        var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, agora, default);
        var lead = conversa1.Lead;
        var dataContato = agora.AddHours(-4);
        lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
        lead.RegistrarContato("Multi Conversas", "11933330003", "multi@teste.local", dataContato);

        var conversa2 = Conversa.Nova(conversa2Id, Canais.Web, agora);
        conversa2.ReapontarLead(lead);
        db.Conversas.Add(conversa2);

        await db.SaveChangesAsync();
        var leadId = lead.Id;

        var resultado = await repo.ExcluirApenasConversaAsync(conversa1Id, default);
        Assert.NotNull(resultado);
        Assert.False(resultado.LeadExcluido);

        Assert.False(await db.Conversas.AnyAsync(c => c.Id == conversa1Id));
        Assert.True(await db.Conversas.AnyAsync(c => c.Id == conversa2Id));

        var leadBanco = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
        Assert.NotNull(leadBanco.ContatoEm);
        Assert.Equal(
            dataContato.ToString("yyyy-MM-ddTHH:mm:ss"),
            leadBanco.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"));

        var controller = CriarController(db);
        var resultadoGet = await controller.Obter(conversa2Id, default);
        var okGet = Assert.IsType<OkObjectResult>(resultadoGet.Result);
        var respostaGet = Assert.IsType<ConversaResponse>(okGet.Value);
        Assert.NotNull(respostaGet.ContatoEm);
        Assert.Equal(leadBanco.ContatoEm.Value, respostaGet.ContatoEm.Value);

        await repo.ExcluirLeadAsync(leadId, default);
    }

    [Fact]
    public async Task Expurgo_remove_lead_antigo_com_seu_contatoEm_e_preserva_lead_recente()
    {
        var conexao = PostgresTestDatabase.ObterConexaoParaAplicacao();
        var agoraFixo = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var travas = new TravaDeConversas();

        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Expurgo:IntervaloVarredura"] = "1.00:00:00",
                ["Expurgo:PrazoRetencaoMeses"] = "12",
            })
            .Build();

        var servicos = new ServiceCollection();
        servicos.AddDbContext<SolarDbContext>(opcoes => opcoes.UseNpgsql(conexao));
        servicos.AddScoped<ConversaRepositorio>();
        servicos.AddSingleton(travas);
        servicos.AddSingleton<IConfiguration>(configuracao);
        servicos.AddSingleton<TimeProvider>(new RelogioFixo(agoraFixo));
        servicos.AddLogging(opcoes => opcoes.AddProvider(NullLoggerProvider.Instance));
        servicos.AddSingleton<ServicoDeExpurgo>();

        await using var provedor = servicos.BuildServiceProvider();
        var servicoExpurgo = provedor.GetRequiredService<ServicoDeExpurgo>();

        var contatoAntigo = agoraFixo.AddMonths(-14);
        var contatoRecente = agoraFixo.AddMonths(-2);

        Guid leadAntigoId;
        Guid leadRecenteId;
        var conversaAntigaId = Guid.NewGuid();
        var conversaRecenteId = Guid.NewGuid();

        await using (var db = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(db);

            var conversaAntiga = await repo.ObterOuCriarAsync(conversaAntigaId, contatoAntigo, default);
            leadAntigoId = conversaAntiga.LeadId;
            conversaAntiga.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, contatoAntigo);
            conversaAntiga.Lead.RegistrarContato("Lead Antigo", "11944440004", "antigo@teste.local", contatoAntigo);
            conversaAntiga.RegistrarTurno(
                "Quero apartamento",
                new TurnoResponse("Resposta", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                contatoAntigo);

            var conversaRecente = await repo.ObterOuCriarAsync(conversaRecenteId, contatoRecente, default);
            leadRecenteId = conversaRecente.LeadId;
            conversaRecente.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, contatoRecente);
            conversaRecente.Lead.RegistrarContato("Lead Recente", "11955550005", "recente@teste.local", contatoRecente);
            conversaRecente.RegistrarTurno(
                "Quero casa",
                new TurnoResponse("Resposta", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                contatoRecente);

            await db.SaveChangesAsync();
        }

        try
        {
            var expurgados = await servicoExpurgo.ExecutarCicloAsync();
            Assert.True(expurgados >= 1);

            await using var dbVerificacao = await PostgresTestDatabase.CriarContextoAsync();
            Assert.False(await dbVerificacao.Leads.AnyAsync(l => l.Id == leadAntigoId));

            var leadRecenteBanco = await dbVerificacao.Leads.AsNoTracking().SingleAsync(l => l.Id == leadRecenteId);
            Assert.NotNull(leadRecenteBanco.ContatoEm);
            Assert.Equal(
                contatoRecente.ToString("yyyy-MM-ddTHH:mm:ss"),
                leadRecenteBanco.ContatoEm.Value.ToString("yyyy-MM-ddTHH:mm:ss"));
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaAntigaId || c.Id == conversaRecenteId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadAntigoId || l.Id == leadRecenteId).ExecuteDeleteAsync();
        }
    }

    private static ConversasController CriarController(SolarDbContext db)
    {
        var agenda = new AgendaRepositorio(db);
        var conversasRepo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);
        var travas = new TravaDeConversas();

        var http = new HttpClient(new AgenteThrowingHandler())
        {
            BaseAddress = new Uri("http://agente.test")
        };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);

        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Conversas:JanelaHistorico"] = "20"
            })
            .Build();

        return new ConversasController(
            conversasRepo,
            encaminhamentosRepo,
            agenda,
            gravacao,
            travas,
            agente,
            configuracao,
            new AmbienteTeste(),
            NullLogger<ConversasController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static Corretor CriarCorretor(DateTimeOffset agora)
    {
        var id = Guid.NewGuid();
        var corretor = Corretor.NovaConta(
            nome: $"Corretor T5 {id:N}",
            email: $"corretor-t5-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-T5-{id:N}@TESTE.COM",
            telefone: "11999999999",
            senhaHash: "fake-hash",
            perfil: PerfisDoPainel.Corretor,
            regioes: ["Sul"],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: "v1",
            em: agora);
        corretor.Aprovar(agora);
        return corretor;
    }

    private sealed class AgenteThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Agente nao deve ser chamado em testes de contato.");
    }

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Solar.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }
}
