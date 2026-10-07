using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed class AgendamentoPorBotaoConcorrenciaPostgresTeste
{
    [Fact]
    public async Task Dois_leads_disputando_o_mesmo_slot_confirmam_um_unico_agendamento()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();
        Guid lead1Id, lead2Id;
        var corretor = CriarCorretor(agora);

        var slotComum = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slotAlt1 = Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));
        var slotAlt2 = Slot.Novo(corretor.Id, agora.AddDays(4), agora.AddDays(4).AddHours(1));
        var slotAlt3 = Slot.Novo(corretor.Id, agora.AddDays(5), agora.AddDays(5).AddHours(1));
        var slotAlt4 = Slot.Novo(corretor.Id, agora.AddDays(6), agora.AddDays(6).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slotComum, slotAlt1, slotAlt2, slotAlt3, slotAlt4);

            var repo = new ConversaRepositorio(preparacao);

            var c1 = await repo.ObterOuCriarAsync(conversa1Id, agora, default);
            lead1Id = c1.LeadId;
            c1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c1.Lead.RegistrarContato("Lead Concorrente 1", "11999991111", null, agora);

            var c2 = await repo.ObterOuCriarAsync(conversa2Id, agora, default);
            lead2Id = c2.LeadId;
            c2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c2.Lead.RegistrarContato("Lead Concorrente 2", "11999992222", null, agora);

            preparacao.Encaminhamentos.AddRange(
                Encaminhamento.Novo(c1.Id, lead1Id, corretor.Id, corretor.Especialidade, agora),
                Encaminhamento.Novo(c2.Id, lead2Id, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            var travas = new TravaDeConversas();
            var agenteHandler = new AgenteContadorHandler();
            var barreira = new BarreiraReservaInterceptor();

            var conexao = PostgresTestDatabase.ObterConexaoParaAplicacao();

            var options1 = new DbContextOptionsBuilder<SolarDbContext>()
                .UseNpgsql(conexao)
                .AddInterceptors(barreira)
                .Options;

            var options2 = new DbContextOptionsBuilder<SolarDbContext>()
                .UseNpgsql(conexao)
                .AddInterceptors(barreira)
                .Options;

            await using var db1 = new SolarDbContext(options1);
            await using var db2 = new SolarDbContext(options2);

            var controller1 = CriarController(db1, travas, agenteHandler);
            var controller2 = CriarController(db2, travas, agenteHandler);

            var resultados = await Task.WhenAll(
                controller1.RegistrarAgendamento(conversa1Id, new AgendamentoRequest(slotComum.Id), default),
                controller2.RegistrarAgendamento(conversa2Id, new AgendamentoRequest(slotComum.Id), default));

            Assert.Equal(2, barreira.ComandosObservados);

            var resultado1 = resultados[0];
            var resultado2 = resultados[1];

            var r1EhOk = resultado1.Result is OkObjectResult;
            var r2EhOk = resultado2.Result is OkObjectResult;

            Assert.True(r1EhOk ^ r2EhOk);

            var vencedorConversaId = r1EhOk ? conversa1Id : conversa2Id;
            var perdedorConversaId = r1EhOk ? conversa2Id : conversa1Id;
            var leadVencedorId = r1EhOk ? lead1Id : lead2Id;

            var resultadoOk = r1EhOk ? (OkObjectResult)resultado1.Result! : (OkObjectResult)resultado2.Result!;
            var resultadoConflito = r1EhOk ? (ObjectResult)resultado2.Result! : (ObjectResult)resultado1.Result!;

            var agendamentoOk = Assert.IsType<AgendamentoDaConversa>(resultadoOk.Value);
            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamentoOk.Estado);
            Assert.NotNull(agendamentoOk.Horario);
            Assert.Equal(slotComum.Id, agendamentoOk.Horario.Id);
            Assert.Empty(agendamentoOk.Alternativas);

            Assert.Equal(StatusCodes.Status409Conflict, resultadoConflito.StatusCode);
            var problem = Assert.IsType<ProblemDetails>(resultadoConflito.Value);
            Assert.Equal("horario indisponivel", problem.Title);
            Assert.Equal("about:blank", problem.Type);
            Assert.Equal("horario_indisponivel", problem.Extensions["codigo"]);

            var oferta = Assert.IsAssignableFrom<IReadOnlyList<SlotOferecido>>(problem.Extensions["oferta"]);
            Assert.Equal(3, oferta.Count);
            Assert.DoesNotContain(oferta, s => s.Id == slotComum.Id);
            Assert.Equal(slotAlt1.Id, oferta[0].Id);
            Assert.Equal(slotAlt2.Id, oferta[1].Id);
            Assert.Equal(slotAlt3.Id, oferta[2].Id);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();

            var slotSalvo = await verificacao.Slots.SingleAsync(s => s.Id == slotComum.Id);
            Assert.Equal(leadVencedorId, slotSalvo.LeadId);

            var totalConfirmadas = await verificacao.Mensagens
                .CountAsync(m => (m.ConversaId == conversa1Id || m.ConversaId == conversa2Id)
                              && m.StatusAgendamento == EstadosDoAgendamento.Confirmado);
            Assert.Equal(1, totalConfirmadas);

            var msgsVencedor = await verificacao.Mensagens
                .Where(m => m.ConversaId == vencedorConversaId)
                .OrderBy(m => m.Id)
                .ToListAsync();
            Assert.Equal(2, msgsVencedor.Count);
            Assert.Equal(Papeis.Lead, msgsVencedor[0].Papel);
            Assert.Equal(Papeis.Agente, msgsVencedor[1].Papel);
            Assert.Equal(EstadosDoAgendamento.Confirmado, msgsVencedor[1].StatusAgendamento);
            Assert.Equal(slotComum.Id, msgsVencedor[1].SlotId);

            var msgsPerdedor = await verificacao.Mensagens
                .Where(m => m.ConversaId == perdedorConversaId)
                .ToListAsync();
            Assert.Empty(msgsPerdedor);

            var resGetVencedor = await controller1.Obter(vencedorConversaId, default);
            var okGetVencedor = Assert.IsType<OkObjectResult>(resGetVencedor.Result);
            var conversaVencedor = Assert.IsType<ConversaResponse>(okGetVencedor.Value);
            Assert.Equal(2, conversaVencedor.Mensagens.Count);
            Assert.NotNull(conversaVencedor.Mensagens[1].Agendamento);
            Assert.Equal(EstadosDoAgendamento.Confirmado, conversaVencedor.Mensagens[1].Agendamento!.Estado);
            Assert.Empty(conversaVencedor.Oferta);

            var resGetPerdedor = await controller2.Obter(perdedorConversaId, default);
            var okGetPerdedor = Assert.IsType<OkObjectResult>(resGetPerdedor.Result);
            var conversaPerdedor = Assert.IsType<ConversaResponse>(okGetPerdedor.Value);
            Assert.Empty(conversaPerdedor.Mensagens);
            Assert.Equal(3, conversaPerdedor.Oferta.Count);
            Assert.DoesNotContain(conversaPerdedor.Oferta, s => s.Id == slotComum.Id);

            Assert.Equal(0, agenteHandler.Chamadas);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            var conversaIds = new[] { conversa1Id, conversa2Id };
            var leadIds = new[] { lead1Id, lead2Id };
            await limpeza.Mensagens.Where(m => conversaIds.Contains(m.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => conversaIds.Contains(e.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => conversaIds.Contains(c.Id)).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Dois_cliques_na_mesma_conversa_nao_reservam_dois_slots()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        var slot1 = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slot1, slot2);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Clique Duplo", "11999993333", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            var travas = new TravaDeConversas();
            var agenteHandler = new AgenteContadorHandler();

            await using var db1 = await PostgresTestDatabase.CriarContextoAsync();
            await using var db2 = await PostgresTestDatabase.CriarContextoAsync();

            var controller1 = CriarController(db1, travas, agenteHandler);
            var controller2 = CriarController(db2, travas, agenteHandler);

            var resultados = await Task.WhenAll(
                controller1.RegistrarAgendamento(conversaId, new AgendamentoRequest(slot1.Id), default),
                controller2.RegistrarAgendamento(conversaId, new AgendamentoRequest(slot2.Id), default));

            var r1EhOk = resultados[0].Result is OkObjectResult;
            var r2EhOk = resultados[1].Result is OkObjectResult;

            Assert.True(r1EhOk ^ r2EhOk);

            var resultadoOk = r1EhOk ? (OkObjectResult)resultados[0].Result! : (OkObjectResult)resultados[1].Result!;
            var resultadoConflito = r1EhOk ? (ObjectResult)resultados[1].Result! : (ObjectResult)resultados[0].Result!;

            var agendamentoOk = Assert.IsType<AgendamentoDaConversa>(resultadoOk.Value);
            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamentoOk.Estado);

            Assert.Equal(StatusCodes.Status409Conflict, resultadoConflito.StatusCode);
            var problem = Assert.IsType<ProblemDetails>(resultadoConflito.Value);
            Assert.Equal("agendamento ja confirmado", problem.Title);
            Assert.Equal("agendamento_ja_confirmado", problem.Extensions["codigo"]);
            Assert.False(problem.Extensions.ContainsKey("oferta"));

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();

            var slotsReservados = await verificacao.Slots
                .Where(s => (s.Id == slot1.Id || s.Id == slot2.Id) && s.LeadId == leadId)
                .ToListAsync();
            Assert.Single(slotsReservados);

            var slotLivre = await verificacao.Slots
                .Where(s => (s.Id == slot1.Id || s.Id == slot2.Id) && s.LeadId == null)
                .ToListAsync();
            Assert.Single(slotLivre);

            var totalMensagens = await verificacao.Mensagens.CountAsync(m => m.ConversaId == conversaId);
            Assert.Equal(2, totalMensagens);

            Assert.Equal(0, agenteHandler.Chamadas);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    private static ConversasController CriarController(
        SolarDbContext db,
        TravaDeConversas travas,
        AgenteContadorHandler agenteHandler)
    {
        var agenda = new AgendaRepositorio(db);
        var conversasRepo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var http = new HttpClient(agenteHandler)
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
            nome: $"Corretor Concorrencia {id:N}",
            email: $"corretor-conc-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-CONC-{id:N}@TESTE.COM",
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

    private sealed class BarreiraReservaInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _doisComandos = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _comandos;

        public int ComandosObservados => _comandos;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (EhUpdateSlotLeadId(sql))
            {
                var count = Interlocked.Increment(ref _comandos);
                if (count == 2)
                {
                    _doisComandos.TrySetResult();
                }
                else if (count == 1)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await _doisComandos.Task.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new TimeoutException("Timeout aguardando segundo comando de reserva na barreira SQL.");
                    }
                }
            }

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static bool EhUpdateSlotLeadId(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return false;
            var s = sql.ToLowerInvariant();
            return s.Contains("update") && s.Contains("slots") && s.Contains("lead_id");
        }
    }

    private sealed class AgenteContadorHandler : HttpMessageHandler
    {
        public int Chamadas => _chamadas;
        private int _chamadas;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _chamadas);
            throw new InvalidOperationException("Agente nao deve ser chamado em T3b.");
        }
    }

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Solar.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
