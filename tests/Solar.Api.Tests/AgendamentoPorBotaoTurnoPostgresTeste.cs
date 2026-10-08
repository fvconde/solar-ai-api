using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
public sealed class AgendamentoPorBotaoTurnoPostgresTeste
{
    [Fact]
    public async Task Conversa_com_agendamento_por_botao_envia_flag_confirmada_ao_turno_em_contexto_novo()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        var fusoSp = TimeSpan.FromHours(-3);
        var agoraSp = agora.ToOffset(fusoSp);
        var inicioBase = new DateTimeOffset(agoraSp.Year, agoraSp.Month, agoraSp.Day, 14, 0, 0, fusoSp).ToUniversalTime().AddDays(2);

        var slot1 = Slot.Novo(corretor.Id, inicioBase, inicioBase.AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, inicioBase.AddDays(1), inicioBase.AddDays(1).AddHours(1));
        var slot3 = Slot.Novo(corretor.Id, inicioBase.AddDays(2), inicioBase.AddDays(2).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slot1, slot2, slot3);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora.AddHours(-1), default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-1));
            conversa.Lead.RegistrarContato("Lead Turno Flag", "11999991234", null, agora.AddHours(-1));

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora.AddHours(-1)));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            var travas = new TravaDeConversas();

            await using (var dbReserva = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controllerReserva = CriarController(dbReserva, travas, new CapturadorTurnoHandler());
                var resReserva = await controllerReserva.RegistrarAgendamento(
                    conversaId, new AgendamentoRequest(slot1.Id), default);

                var okReserva = Assert.IsType<OkObjectResult>(resReserva.Result);
                var agendamento = Assert.IsType<AgendamentoDaConversa>(okReserva.Value);
                Assert.Equal(EstadosDoAgendamento.Confirmado, agendamento.Estado);
            }

            var handlerTurno = new CapturadorTurnoHandler();

            await using (var dbNovo = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controllerNovo = CriarController(dbNovo, travas, handlerTurno);
                var resEnviar = await controllerNovo.Enviar(
                    conversaId, new NovaMensagemRequest("Gostaria de tirar uma duvida sobre a vaga de garagem"), default);

                var okEnviar = Assert.IsType<OkObjectResult>(resEnviar.Result);
                var mensagemResp = Assert.IsType<MensagemResponse>(okEnviar.Value);
                Assert.NotNull(mensagemResp);
            }

            Assert.Equal(1, handlerTurno.Chamadas);
            Assert.NotNull(handlerTurno.UltimoTurnoRequest);
            Assert.True(handlerTurno.UltimoTurnoRequest.VisitaConfirmada);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();

            var slot1Salvo = await verificacao.Slots.SingleAsync(s => s.Id == slot1.Id);
            Assert.Equal(leadId, slot1Salvo.LeadId);

            var outrosSlots = await verificacao.Slots.Where(s => s.Id == slot2.Id || s.Id == slot3.Id).ToListAsync();
            Assert.All(outrosSlots, s => Assert.Null(s.LeadId));

            var totalConfirmadas = await verificacao.Mensagens
                .CountAsync(m => m.ConversaId == conversaId && m.StatusAgendamento == EstadosDoAgendamento.Confirmado);
            Assert.Equal(1, totalConfirmadas);

            var totalEncaminhamentos = await verificacao.Encaminhamentos
                .CountAsync(e => e.ConversaId == conversaId);
            Assert.Equal(1, totalEncaminhamentos);
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

    [Fact]
    public async Task Conversa_sem_agendamento_envia_flag_falsa_ao_turno_em_contexto_novo()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        var fusoSp = TimeSpan.FromHours(-3);
        var agoraSp = agora.ToOffset(fusoSp);
        var inicioBase = new DateTimeOffset(agoraSp.Year, agoraSp.Month, agoraSp.Day, 14, 0, 0, fusoSp).ToUniversalTime().AddDays(2);

        var slot1 = Slot.Novo(corretor.Id, inicioBase, inicioBase.AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, inicioBase.AddDays(1), inicioBase.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slot1, slot2);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora.AddHours(-1), default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-1));
            conversa.Lead.RegistrarContato("Lead Sem Agendamento", "11999995678", null, agora.AddHours(-1));

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora.AddHours(-1)));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            var travas = new TravaDeConversas();
            var handlerTurno = new CapturadorTurnoHandler();

            await using (var dbNovo = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controllerNovo = CriarController(dbNovo, travas, handlerTurno);
                var resEnviar = await controllerNovo.Enviar(
                    conversaId, new NovaMensagemRequest("Quero mais informacoes"), default);

                var okEnviar = Assert.IsType<OkObjectResult>(resEnviar.Result);
                var mensagemResp = Assert.IsType<MensagemResponse>(okEnviar.Value);
                Assert.NotNull(mensagemResp);
            }

            Assert.Equal(1, handlerTurno.Chamadas);
            Assert.NotNull(handlerTurno.UltimoTurnoRequest);
            Assert.False(handlerTurno.UltimoTurnoRequest.VisitaConfirmada);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slots = await verificacao.Slots.Where(s => s.CorretorId == corretor.Id).ToListAsync();
            Assert.All(slots, s => Assert.Null(s.LeadId));

            var totalConfirmadas = await verificacao.Mensagens
                .CountAsync(m => m.ConversaId == conversaId && m.StatusAgendamento == EstadosDoAgendamento.Confirmado);
            Assert.Equal(0, totalConfirmadas);
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
        CapturadorTurnoHandler agenteHandler)
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
            nome: $"Corretor Turno {id:N}",
            email: $"corretor-turno-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-TURNO-{id:N}@TESTE.COM",
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

    private sealed class CapturadorTurnoHandler : HttpMessageHandler
    {
        public TurnoRequest? UltimoTurnoRequest { get; private set; }
        public int Chamadas => _chamadas;
        private int _chamadas;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _chamadas);

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            UltimoTurnoRequest = JsonSerializer.Deserialize<TurnoRequest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var fakeResponse = new TurnoResponse(
                Resposta: "Perfeito, continue me falando sobre o que procura.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(fakeResponse)
            };
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
