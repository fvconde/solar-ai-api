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
public sealed class OfertaAgendamentoPostgresTeste
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    public async Task Get_oferta_elegibilidade_combinacoes(
        bool corretorAtribuido,
        bool temContato,
        bool temConfirmacao,
        bool esperaOfertaPreenchida)
    {
        var conversaId = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        Guid leadId;
        Corretor? corretor = null;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);

            if (temContato)
            {
                conversa.Lead.RegistrarContato("Lead Teste", "11999999999", null, agora);
            }

            if (corretorAtribuido)
            {
                corretor = CriarCorretor(agora);
                preparacao.Corretores.Add(corretor);
                preparacao.Encaminhamentos.Add(
                    Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

                preparacao.Slots.AddRange(
                    Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1)),
                    Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1)),
                    Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1)));
            }

            if (temConfirmacao)
            {
                var msg = Mensagem.DaLia(conversa.Id, "Confirmado", ProximasAcoes.AgendarReuniao, agora);
                msg.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, slotId: null);
                preparacao.Mensagens.Add(msg);
            }

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resultado = await controller.Obter(conversaId, default);
            var ok = Assert.IsType<OkObjectResult>(resultado.Result);
            var resposta = Assert.IsType<ConversaResponse>(ok.Value);

            if (esperaOfertaPreenchida)
            {
                Assert.Equal(3, resposta.Oferta.Count);
                Assert.All(resposta.Oferta, s => Assert.True(s.Inicio > agora));
            }
            else
            {
                Assert.Empty(resposta.Oferta);
            }
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            if (corretor != null)
            {
                await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            }
            await limpeza.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            if (corretor != null)
            {
                await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
            }
        }
    }

    [Fact]
    public async Task Post_contato_retorna_oferta_apenas_quando_elegivel()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaComCorretorId = Guid.NewGuid();
        var conversaSemCorretorId = Guid.NewGuid();
        var conversaComConfirmacaoId = Guid.NewGuid();
        Guid lead1, lead2, lead3;
        var corretor = CriarCorretor(agora);

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(
                Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1)),
                Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1)),
                Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1)));

            var repo = new ConversaRepositorio(preparacao);

            var c1 = await repo.ObterOuCriarAsync(conversaComCorretorId, agora, default);
            lead1 = c1.LeadId;
            c1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c1.Id, lead1, corretor.Id, corretor.Especialidade, agora));

            var c2 = await repo.ObterOuCriarAsync(conversaSemCorretorId, agora, default);
            lead2 = c2.LeadId;
            c2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);

            var c3 = await repo.ObterOuCriarAsync(conversaComConfirmacaoId, agora, default);
            lead3 = c3.LeadId;
            c3.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c3.Id, lead3, corretor.Id, corretor.Especialidade, agora));
            var msgConfirmada = Mensagem.DaLia(c3.Id, "Confirmado", ProximasAcoes.AgendarReuniao, agora);
            msgConfirmada.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            preparacao.Mensagens.Add(msgConfirmada);

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resComCorretor = await controller.RegistrarContato(
                conversaComCorretorId, new ContatoRequest("Lead 1", "11999990001", null), default);
            var ok1 = Assert.IsType<OkObjectResult>(resComCorretor.Result);
            var cont1 = Assert.IsType<ContatoResponse>(ok1.Value);
            Assert.Equal(lead1, cont1.LeadId);
            Assert.Equal(3, cont1.Oferta.Count);

            var resSemCorretor = await controller.RegistrarContato(
                conversaSemCorretorId, new ContatoRequest("Lead 2", "11999990002", null), default);
            var ok2 = Assert.IsType<OkObjectResult>(resSemCorretor.Result);
            var cont2 = Assert.IsType<ContatoResponse>(ok2.Value);
            Assert.Equal(lead2, cont2.LeadId);
            Assert.Empty(cont2.Oferta);

            var resComConfirmacao = await controller.RegistrarContato(
                conversaComConfirmacaoId, new ContatoRequest("Lead 3", "11999990003", null), default);
            var ok3 = Assert.IsType<OkObjectResult>(resComConfirmacao.Result);
            var cont3 = Assert.IsType<ContatoResponse>(ok3.Value);
            Assert.Equal(lead3, cont3.LeadId);
            Assert.Empty(cont3.Oferta);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            var ids = new[] { conversaComCorretorId, conversaSemCorretorId, conversaComConfirmacaoId };
            var leadIds = new[] { lead1, lead2, lead3 };
            await limpeza.Mensagens.Where(m => ids.Contains(m.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => ids.Contains(e.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Proximos_tres_ordenados_em_ambas_as_rotas()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        var conversaPostId = Guid.NewGuid();
        Guid leadId, leadPostId;
        var leadReservado = Lead.Novo(agora);
        var corretorA = CriarCorretor(agora);
        var corretorB = CriarCorretor(agora);

        var slot4 = Slot.Novo(corretorA.Id, agora.AddDays(4), agora.AddDays(4).AddHours(1));
        var slot3 = Slot.Novo(corretorA.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));
        var slot2 = Slot.Novo(corretorA.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slot1 = Slot.Novo(corretorA.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        var slotPassado = Slot.Novo(corretorA.Id, agora.AddDays(-2), agora.AddDays(-2).AddHours(1));
        var slotReservado = Slot.Novo(corretorA.Id, agora.AddHours(12), agora.AddHours(13));

        var slotB1 = Slot.Novo(corretorB.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        var slotB2 = Slot.Novo(corretorB.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Leads.Add(leadReservado);
            preparacao.Corretores.AddRange(corretorA, corretorB);

            preparacao.Slots.AddRange(
                slot4,
                slot3,
                slot2,
                slot1,
                slotPassado,
                slotReservado,
                slotB1,
                slotB2);
            await preparacao.SaveChangesAsync();

            await preparacao.Slots
                .Where(s => s.Id == slotReservado.Id && s.LeadId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, leadReservado.Id));

            var repo = new ConversaRepositorio(preparacao);

            var cGet = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = cGet.LeadId;
            cGet.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            cGet.Lead.RegistrarContato("Lead Get", "11988880001", null, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(cGet.Id, leadId, corretorA.Id, corretorA.Especialidade, agora));

            var cPost = await repo.ObterOuCriarAsync(conversaPostId, agora, default);
            leadPostId = cPost.LeadId;
            cPost.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(cPost.Id, leadPostId, corretorA.Id, corretorA.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resGet = await controller.Obter(conversaId, default);
            var okGet = Assert.IsType<OkObjectResult>(resGet.Result);
            var getResp = Assert.IsType<ConversaResponse>(okGet.Value);

            var esperadosIds = new[] { slot1.Id, slot2.Id, slot3.Id };
            Assert.Equal(esperadosIds, getResp.Oferta.Select(s => s.Id));

            var resPost = await controller.RegistrarContato(
                conversaPostId, new ContatoRequest("Lead Post", "11988880002", null), default);
            var okPost = Assert.IsType<OkObjectResult>(resPost.Result);
            var postResp = Assert.IsType<ContatoResponse>(okPost.Value);

            Assert.Equal(esperadosIds, postResp.Oferta.Select(s => s.Id));
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretorA.Id || s.CorretorId == corretorB.Id).ExecuteDeleteAsync();
            var ids = new[] { conversaId, conversaPostId };
            var leadIds = new[] { leadId, leadPostId, leadReservado.Id };
            await limpeza.Encaminhamentos.Where(e => ids.Contains(e.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretorA.Id || c.Id == corretorB.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Recalculo_no_get_ao_reservar_ou_vencer_slot()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var outroLead = Lead.Novo(agora);
        var corretor = CriarCorretor(agora);

        var slot1 = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slot3 = Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));
        var slot4 = Slot.Novo(corretor.Id, agora.AddDays(4), agora.AddDays(4).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Leads.Add(outroLead);
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slot1, slot2, slot3, slot4);

            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c.Lead.RegistrarContato("Lead Teste", "11977770001", null, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using (var db1 = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controller1 = CriarController(db1);
                var res1 = await controller1.Obter(conversaId, default);
                var ok1 = Assert.IsType<OkObjectResult>(res1.Result);
                var resp1 = Assert.IsType<ConversaResponse>(ok1.Value);
                Assert.Equal(new[] { slot1.Id, slot2.Id, slot3.Id }, resp1.Oferta.Select(s => s.Id));
            }

            await using (var dbReserva = await PostgresTestDatabase.CriarContextoAsync())
            {
                await dbReserva.Slots
                    .Where(s => s.Id == slot1.Id && s.LeadId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, outroLead.Id));
            }

            await using (var db2 = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controller2 = CriarController(db2);
                var res2 = await controller2.Obter(conversaId, default);
                var ok2 = Assert.IsType<OkObjectResult>(res2.Result);
                var resp2 = Assert.IsType<ConversaResponse>(ok2.Value);
                Assert.Equal(new[] { slot2.Id, slot3.Id, slot4.Id }, resp2.Oferta.Select(s => s.Id));
            }

            await using (var dbVencimento = await PostgresTestDatabase.CriarContextoAsync())
            {
                await dbVencimento.Slots
                    .Where(s => s.Id == slot2.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.Inicio, agora.AddDays(-5))
                        .SetProperty(x => x.Fim, agora.AddDays(-5).AddHours(1)));
            }

            await using (var db3 = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controller3 = CriarController(db3);
                var res3 = await controller3.Obter(conversaId, default);
                var ok3 = Assert.IsType<OkObjectResult>(res3.Result);
                var resp3 = Assert.IsType<ConversaResponse>(ok3.Value);
                Assert.Equal(new[] { slot3.Id, slot4.Id }, resp3.Oferta.Select(s => s.Id));
            }
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            var leadIds = new[] { leadId, outroLead.Id };
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Lista_vazia_sem_slots_e_formato_json()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c.Lead.RegistrarContato("Lead Sem Slots", "11966660001", "lead@teste.com", agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c.Id, leadId, corretor.Id, corretor.Especialidade, agora));
            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resGet = await controller.Obter(conversaId, default);
            var okGet = Assert.IsType<OkObjectResult>(resGet.Result);
            var getResp = Assert.IsType<ConversaResponse>(okGet.Value);

            Assert.Equal(conversaId, getResp.ConversaId);
            Assert.NotNull(getResp.PerfilLead);
            Assert.False(getResp.ContatoPendente);
            Assert.NotNull(getResp.ConsentimentoEm);
            Assert.Equal(AvisoPrivacidade.VersaoAtual, getResp.VersaoAvisoPrivacidade);
            Assert.Empty(getResp.Oferta);

            var jsonWeb = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var jsonGet = JsonSerializer.Serialize(getResp, jsonWeb);
            Assert.Contains("\"oferta\":[]", jsonGet);

            var resPost = await controller.RegistrarContato(
                conversaId, new ContatoRequest("Lead Sem Slots", "11966660001", "lead@teste.com"), default);
            var okPost = Assert.IsType<OkObjectResult>(resPost.Result);
            var postResp = Assert.IsType<ContatoResponse>(okPost.Value);

            Assert.Equal(leadId, postResp.LeadId);
            Assert.Empty(postResp.Oferta);

            var jsonPost = JsonSerializer.Serialize(postResp, jsonWeb);
            Assert.Contains("\"oferta\":[]", jsonPost);
            Assert.DoesNotContain("11966660001", jsonPost);
            Assert.DoesNotContain("lead@teste.com", jsonPost);
            Assert.DoesNotContain("Lead Sem Slots", jsonPost);

            var slotFormat = new SlotOferecido(
                999,
                new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero));
            var respostaComSlot = new ContatoResponse(leadId, [slotFormat]);
            var jsonFormat = JsonSerializer.Serialize(respostaComSlot, jsonWeb);

            Assert.Contains("2026-10-07T17:00:00", jsonFormat);
            Assert.Contains("2026-10-07T18:00:00", jsonFormat);
            Assert.Contains("\"id\":999", jsonFormat);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Preserva_404_para_inexistente_e_400_para_contato_invalido()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaValidaId = Guid.NewGuid();
        Guid leadId;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaValidaId, agora, default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var getInexistente = await controller.Obter(Guid.NewGuid(), default);
            var objGet404 = Assert.IsAssignableFrom<ObjectResult>(getInexistente.Result);
            Assert.Equal(StatusCodes.Status404NotFound, objGet404.StatusCode);

            var postInexistente = await controller.RegistrarContato(
                Guid.NewGuid(), new ContatoRequest("Nome", "11999999999", null), default);
            var objPost404 = Assert.IsAssignableFrom<ObjectResult>(postInexistente.Result);
            Assert.Equal(StatusCodes.Status404NotFound, objPost404.StatusCode);

            var postInvalido = await controller.RegistrarContato(
                conversaValidaId, new ContatoRequest("Apenas Nome", null, null), default);
            var objPost400 = Assert.IsAssignableFrom<ObjectResult>(postInvalido.Result);
            Assert.Equal(StatusCodes.Status400BadRequest, objPost400.StatusCode);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaValidaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
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
            nome: $"Corretor T2 {id:N}",
            email: $"corretor-t2-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-T2-{id:N}@TESTE.COM",
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
            throw new InvalidOperationException("Agente nao deve ser chamado em T2.");
    }

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Solar.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
