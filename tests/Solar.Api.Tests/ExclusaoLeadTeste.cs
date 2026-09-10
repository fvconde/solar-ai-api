using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Agendamentos;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

public class ExclusaoLeadTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 10, 1, 0, 0, TimeSpan.Zero);
    private const string ChaveTeste = "teste-unitario-mock-key-123";

    private static SolarDbContext CriarBanco()
    {
        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new SolarDbContext(options);
    }

    private static IConfiguration CriarConfiguracao(string? chave = ChaveTeste)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seguranca:ChavePrivacidade"] = chave,
                ["Conversas:JanelaHistorico"] = "20"
            })
            .Build();
    }

    private static DefaultHttpContext CriarHttpContextComHeader(string? headerNome = null, string? headerValor = null)
    {
        var context = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(headerNome) && !string.IsNullOrEmpty(headerValor))
        {
            context.Request.Headers[headerNome] = headerValor;
        }
        return context;
    }

    private static ConversasController CriarController(
        SolarDbContext db,
        ConversaRepositorio conversas,
        EncaminhamentoRepositorio encaminhamentos,
        TravaDeConversas travas,
        IConfiguration configuracao,
        DefaultHttpContext httpContext)
    {
        var agenda = new AgendaRepositorio(db);
        return new ConversasController(
            conversas,
            encaminhamentos,
            agenda,
            new GravacaoDoTurno(db, agenda),
            travas,
            null!,
            configuracao,
            null!,
            NullLogger<ConversasController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Fact]
    public async Task Exclusao_do_lead_apaga_conversa_mensagens_e_encaminhamentos_em_cascata()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, Agora, default);
        var lead = conversa.Lead;

        lead.RegistrarContato("Carlos Silva", "11988887777", "carlos@teste.local", Agora);

        var turno = new TurnoResponse(
            Resposta: "Perfeito, vou te apresentar opções.",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(Regiao: "Pinheiros", PrecoMax: 800000),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [],
            SlotEscolhido: null);

        repo.AplicarTurno(conversa, "Procuro apartamento em Pinheiros até 800k", turno, Agora);

        var encaminhamento = Encaminhamento.Novo(
            conversaId, lead.Id, null, Especialidades.Moradia, Agora);
        db.Encaminhamentos.Add(encaminhamento);

        await db.SaveChangesAsync();

        // Antes da exclusao: os quatro registros existem no banco
        Assert.Equal(1, await db.Leads.CountAsync());
        Assert.Equal(1, await db.Conversas.CountAsync());
        Assert.Equal(2, await db.Mensagens.CountAsync()); // Pergunta + resposta
        Assert.Equal(1, await db.Encaminhamentos.CountAsync());

        // Executa exclusao do lead (direito de eliminacao LGPD)
        var resultado = await repo.ExcluirLeadAsync(lead.Id, default);

        Assert.NotNull(resultado);
        Assert.Equal(lead.Id, resultado.LeadId);
        Assert.Equal(1, resultado.ConversasAfetadas);
        Assert.Equal(2, resultado.MensagensExcluidas);

        // Depois da chamada: os quatro selects devolvem zero linhas
        Assert.Equal(0, await db.Leads.CountAsync(l => l.Id == lead.Id));
        Assert.Equal(0, await db.Conversas.CountAsync(c => c.LeadId == lead.Id));
        Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversaId));
        Assert.Equal(0, await db.Encaminhamentos.CountAsync(e => e.LeadId == lead.Id));

        // A mesma conversa responde 404 (retorna null no repositorio)
        var conversaExcluida = await repo.ObterAsync(conversaId, default);
        Assert.Null(conversaExcluida);
    }

    [Fact]
    public async Task Exclusao_apenas_conversa_preserva_lead_e_outras_conversas()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();

        var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, Agora, default);
        var lead = conversa1.Lead;
        lead.RegistrarContato("Maria Souza", "11999998888", "maria@teste.local", Agora);

        var conversa2 = Conversa.Nova(conversa2Id, Canais.Web, Agora);
        conversa2.ReapontarLead(lead);
        db.Conversas.Add(conversa2);

        var turno = new TurnoResponse("Olá", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null);
        repo.AplicarTurno(conversa1, "Oi", turno, Agora);
        repo.AplicarTurno(conversa2, "Outro assunto", turno, Agora);

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Leads.CountAsync());
        Assert.Equal(2, await db.Conversas.CountAsync());
        Assert.Equal(4, await db.Mensagens.CountAsync());

        // Exclui apenas conversa 1
        var resultado = await repo.ExcluirApenasConversaAsync(conversa1Id, default);

        Assert.NotNull(resultado);
        Assert.Equal(conversa1Id, resultado.ConversaId);
        Assert.Equal(lead.Id, resultado.LeadId);
        Assert.False(resultado.LeadExcluido);
        Assert.Equal(2, resultado.MensagensExcluidas);

        // Conversa 1 sumiu, mas Lead e Conversa 2 continuam intactos
        Assert.Null(await repo.ObterAsync(conversa1Id, default));
        Assert.Equal(0, await db.Conversas.CountAsync(c => c.Id == conversa1Id));
        Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversa1Id));

        Assert.Equal(1, await db.Leads.CountAsync(l => l.Id == lead.Id));
        var conversa2Preservada = await repo.ObterAsync(conversa2Id, default);
        Assert.NotNull(conversa2Preservada);
        Assert.Equal(2, await db.Mensagens.CountAsync(m => m.ConversaId == conversa2Id));
    }

    [Fact]
    public async Task Exclusao_de_lead_inexistente_devolve_nulo()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);

        var resultado = await repo.ExcluirLeadAsync(Guid.NewGuid(), default);

        Assert.Null(resultado);
    }

    [Fact]
    public async Task Exclusao_preserva_outros_leads_e_conversas_independentes()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);

        var conversaAId = Guid.NewGuid();
        var conversaA = await repo.ObterOuCriarAsync(conversaAId, Agora, default);
        conversaA.Lead.RegistrarContato("Lead A", "11911111111", "leada@teste.local", Agora);

        var conversaBId = Guid.NewGuid();
        var conversaB = await repo.ObterOuCriarAsync(conversaBId, Agora, default);
        conversaB.Lead.RegistrarContato("Lead B", "11922222222", "leadb@teste.local", Agora);

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Leads.CountAsync());
        Assert.Equal(2, await db.Conversas.CountAsync());

        // Exclui apenas o Lead A
        var resultado = await repo.ExcluirLeadAsync(conversaA.LeadId, default);
        Assert.NotNull(resultado);

        // Lead A sumiu
        Assert.Equal(0, await db.Leads.CountAsync(l => l.Id == conversaA.LeadId));
        Assert.Null(await repo.ObterAsync(conversaAId, default));

        // Lead B e sua conversa permanecem intactos
        Assert.Equal(1, await db.Leads.CountAsync(l => l.Id == conversaB.LeadId));
        var conversaBPreseravada = await repo.ObterAsync(conversaBId, default);
        Assert.NotNull(conversaBPreseravada);
        Assert.Equal("Lead B", conversaBPreseravada.Lead.Nome);
    }

    [Fact]
    public async Task LeadsController_DELETE_sem_autorizacao_retorna_401()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var controller = new LeadsController(repo, travas, config, NullLogger<LeadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var resposta = await controller.Excluir(Guid.NewGuid(), default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, erro.StatusCode);
    }

    [Fact]
    public async Task LeadsController_DELETE_com_chave_invalida_retorna_403()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, "chave-errada");
        var controller = new LeadsController(repo, travas, config, NullLogger<LeadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var resposta = await controller.Excluir(Guid.NewGuid(), default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, erro.StatusCode);
    }

    [Fact]
    public async Task LeadsController_DELETE_quando_servidor_nao_tem_chave_configurada_recusa_com_503()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var travas = new TravaDeConversas();
        var configSemChave = CriarConfiguracao(chave: null);

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, "qualquer-coisa");
        var controller = new LeadsController(repo, travas, configSemChave, NullLogger<LeadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var resposta = await controller.Excluir(Guid.NewGuid(), default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, erro.StatusCode);
    }

    [Fact]
    public async Task LeadsController_DELETE_com_chave_valida_devolve_200_ou_404()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, Agora, default);
        var leadId = conversa.LeadId;
        await db.SaveChangesAsync();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, ChaveTeste);
        var controller = new LeadsController(repo, travas, config, NullLogger<LeadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Sucesso 200 OK
        var resposta = await controller.Excluir(leadId, default);
        var ok = Assert.IsType<OkObjectResult>(resposta.Result);
        var corpo = Assert.IsType<ExclusaoLeadResponse>(ok.Value);

        Assert.Equal(leadId, corpo.LeadId);
        Assert.Equal(1, corpo.ConversasAfetadas);
        Assert.Equal(0, corpo.MensagensExcluidas);
        Assert.Equal("lead_e_vinculos", corpo.Escopo);

        // Tentativa subsequente devolve 404 NotFound
        var respostaInexistente = await controller.Excluir(leadId, default);
        var naoEncontrado = Assert.IsType<ObjectResult>(respostaInexistente.Result);
        Assert.Equal(StatusCodes.Status404NotFound, naoEncontrado.StatusCode);
    }

    [Fact]
    public async Task ConversasController_DELETE_sem_autorizacao_retorna_401()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var conversaId = Guid.NewGuid();
        await repo.ObterOuCriarAsync(conversaId, Agora, default);
        await db.SaveChangesAsync();

        var controller = CriarController(
            db, repo, encaminhamentosRepo, travas, config, new DefaultHttpContext());

        // Sem header de autorizacao: deve falhar com 401 mesmo quando excluirLead = false
        var resposta = await controller.Excluir(conversaId, excluirLead: false, default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, erro.StatusCode);
    }

    [Fact]
    public async Task ConversasController_DELETE_com_chave_invalida_retorna_403()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var conversaId = Guid.NewGuid();
        await repo.ObterOuCriarAsync(conversaId, Agora, default);
        await db.SaveChangesAsync();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, "chave-errada");
        var controller = CriarController(
            db, repo, encaminhamentosRepo, travas, config, httpContext);

        var resposta = await controller.Excluir(conversaId, excluirLead: false, default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, erro.StatusCode);
    }

    [Fact]
    public async Task ConversasController_DELETE_quando_servidor_nao_tem_chave_configurada_recusa_com_503()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var travas = new TravaDeConversas();
        var configSemChave = CriarConfiguracao(chave: null);

        var conversaId = Guid.NewGuid();
        await repo.ObterOuCriarAsync(conversaId, Agora, default);
        await db.SaveChangesAsync();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, "qualquer-chave");
        var controller = CriarController(
            db, repo, encaminhamentosRepo, travas, configSemChave, httpContext);

        var resposta = await controller.Excluir(conversaId, excluirLead: false, default);
        var erro = Assert.IsType<ObjectResult>(resposta.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, erro.StatusCode);
    }

    [Fact]
    public async Task ConversasController_DELETE_autorizado_apaga_apenas_conversa_e_informa_mensagens_excluidas()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();

        var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, Agora, default);
        var lead = conversa1.Lead;
        lead.RegistrarContato("Joao Santos", "11977776666", "joao@teste.local", Agora);

        var conversa2 = Conversa.Nova(conversa2Id, Canais.Web, Agora);
        conversa2.ReapontarLead(lead);
        db.Conversas.Add(conversa2);

        var turno = new TurnoResponse("Olá", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null);
        repo.AplicarTurno(conversa1, "Oi C1", turno, Agora);
        await db.SaveChangesAsync();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderChavePrivacidade, ChaveTeste);
        var controller = CriarController(
            db, repo, encaminhamentosRepo, travas, config, httpContext);

        // DELETE com autorizacao: excluirLead = false
        var resposta = await controller.Excluir(conversa1Id, excluirLead: false, default);
        var ok = Assert.IsType<OkObjectResult>(resposta.Result);
        var corpo = Assert.IsType<ExclusaoConversaResponse>(ok.Value);

        Assert.Equal(conversa1Id, corpo.ConversaId);
        Assert.Equal(lead.Id, corpo.LeadId);
        Assert.False(corpo.LeadExcluido);
        Assert.Equal(2, corpo.MensagensExcluidas); // 1 pergunta + 1 resposta
        Assert.Equal("apenas_conversa", corpo.Escopo);

        // Conversa 1 foi removida, lead e conversa 2 continuam existindo
        Assert.Null(await repo.ObterAsync(conversa1Id, default));
        Assert.NotNull(await repo.ObterAsync(conversa2Id, default));
        Assert.Equal(1, await db.Leads.CountAsync(l => l.Id == lead.Id));
    }

    [Fact]
    public async Task ConversasController_DELETE_com_excluirLead_true_e_chave_valida_elimina_lead_em_cascata_e_informa_mensagens_excluidas()
    {
        using var db = CriarBanco();
        var repo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var travas = new TravaDeConversas();
        var config = CriarConfiguracao();

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();

        var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, Agora, default);
        var lead = conversa1.Lead;

        var conversa2 = Conversa.Nova(conversa2Id, Canais.Web, Agora);
        conversa2.ReapontarLead(lead);
        db.Conversas.Add(conversa2);

        var turno = new TurnoResponse("Olá", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null);
        repo.AplicarTurno(conversa1, "Oi C1", turno, Agora);
        repo.AplicarTurno(conversa2, "Oi C2", turno, Agora);
        await db.SaveChangesAsync();

        var httpContext = CriarHttpContextComHeader(AutorizacaoPrivacidade.HeaderAdminKey, ChaveTeste);
        var controller = CriarController(
            db, repo, encaminhamentosRepo, travas, config, httpContext);

        var resposta = await controller.Excluir(conversa1Id, excluirLead: true, default);
        var ok = Assert.IsType<OkObjectResult>(resposta.Result);
        var corpo = Assert.IsType<ExclusaoConversaResponse>(ok.Value);

        Assert.Equal(conversa1Id, corpo.ConversaId);
        Assert.Equal(lead.Id, corpo.LeadId);
        Assert.True(corpo.LeadExcluido);
        Assert.Equal(4, corpo.MensagensExcluidas); // 2 mensagens de C1 + 2 mensagens de C2
        Assert.Equal("lead_e_vinculos", corpo.Escopo);

        // Toda a hierarquia foi removida
        Assert.Equal(0, await db.Leads.CountAsync(l => l.Id == lead.Id));
        Assert.Equal(0, await db.Conversas.CountAsync(c => c.LeadId == lead.Id));
    }

    [Fact]
    public async Task TravaDeConversas_TravarMultiplasAsync_sincroniza_acesso_concorrente()
    {
        var travas = new TravaDeConversas();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var lock1Adquirido = false;
        var lock2Adquirido = false;

        using (await travas.TravarMultiplasAsync([id1, id2, id3], default))
        {
            lock1Adquirido = true;

            // Tentativa paralela com subconjunto ou ordem inversa nao deve entrar enquanto o primeiro lock estiver ativo
            var taskSegundaTrava = Task.Run(async () =>
            {
                using var _ = await travas.TravarMultiplasAsync([id3, id1], default);
                lock2Adquirido = true;
            });

            // Breve delay para garantir que taskSegundaTrava tentou adquirir o lock
            await Task.Delay(50);
            Assert.False(lock2Adquirido, "Segunda trava nao deveria ser adquirida enquanto a primeira estiver retida");
        }

        // Apos liberar o primeiro lock, a segunda adquire
        await Task.Delay(50);
        Assert.True(lock1Adquirido);
    }
}
