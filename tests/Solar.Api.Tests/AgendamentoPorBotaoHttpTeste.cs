using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Solar.Api.Agendamentos;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class AgendamentoPorBotaoHttpTeste(HttpAgendamentoFixture fixture) : IClassFixture<HttpAgendamentoFixture>
{
    [Fact]
    public async Task Http_reserva_aparece_no_reload_na_metrica_e_no_detalhe_do_lead()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora, "CorretorMetrica");
        var corretorToken = TokenSeguro.Criar();

        var fusoSp = TimeSpan.FromHours(-3);
        var agoraSp = agora.ToOffset(fusoSp);
        var inicioBase = new DateTimeOffset(agoraSp.Year, agoraSp.Month, agoraSp.Day, 14, 0, 0, fusoSp).ToUniversalTime().AddDays(2);

        var slot1 = Slot.Novo(corretor.Id, inicioBase, inicioBase.AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, inicioBase.AddDays(1), inicioBase.AddDays(1).AddHours(1));
        var slot3 = Slot.Novo(corretor.Id, inicioBase.AddDays(2), inicioBase.AddDays(2).AddHours(1));
        var slot4 = Slot.Novo(corretor.Id, inicioBase.AddDays(3), inicioBase.AddDays(3).AddHours(1));

        DateTimeOffset? intencaoEm;
        DateTimeOffset? essenciaisEm;
        DateTimeOffset? encaminhadaEm;
        DateTimeOffset? corretorAtribuidoEm;
        int tentativasReengajamento;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.Add(corretor);
            db.Sessoes.Add(SessaoCorretor.Nova(corretor.Id, TokenSeguro.Sha256(corretorToken), agora.AddDays(1)));
            db.Slots.AddRange(slot1, slot2, slot3, slot4);

            var repo = new ConversaRepositorio(db);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora.AddHours(-1), default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-1));
            conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead HTTP Teste", PrecoMax: 500000), agora.AddHours(-1));

            var turnoHandoff = new TurnoResponse(
                "Encaminhando para corretor...",
                Intencoes.Compra,
                new CamposExtraidos(),
                ProximasAcoes.DirecionarEspecialista,
                [],
                null,
                EssenciaisCompletos: true);
            conversa.RegistrarTurno("Quero atendimento", turnoHandoff, agora.AddHours(-1));
            conversa.RegistrarCorretorAtribuido(agora.AddHours(-1));

            db.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora.AddHours(-1)));

            await db.SaveChangesAsync();

            var cPersistida = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            intencaoEm = cPersistida.IntencaoEm;
            essenciaisEm = cPersistida.EssenciaisEm;
            encaminhadaEm = cPersistida.EncaminhadaEm;
            corretorAtribuidoEm = cPersistida.CorretorAtribuidoEm;
            tentativasReengajamento = cPersistida.TentativasReengajamento;
        }

        try
        {
            using var clientPublico = fixture.Factory.CreateClient();

            using var respContato = await clientPublico.PostAsJsonAsync(
                $"/conversas/{conversaId:D}/contato",
                new ContatoRequest("Lead HTTP Teste", "11988880001", "lead.http@teste.local"));
            Assert.Equal(HttpStatusCode.OK, respContato.StatusCode);

            var jsonContato = await respContato.Content.ReadAsStringAsync();
            Assert.Contains("\"leadId\":", jsonContato);
            Assert.Contains("\"oferta\":", jsonContato);

            var contatoResposta = JsonSerializer.Deserialize<ContatoResponse>(
                jsonContato,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.NotNull(contatoResposta);
            Assert.Equal(leadId, contatoResposta.LeadId);
            Assert.Equal(3, contatoResposta.Oferta.Count);
            Assert.Equal(slot1.Id, contatoResposta.Oferta[0].Id);

            using var respGet = await clientPublico.GetAsync($"/conversas/{conversaId:D}");
            Assert.Equal(HttpStatusCode.OK, respGet.StatusCode);
            var getResposta = await respGet.Content.ReadFromJsonAsync<ConversaResponse>();
            Assert.NotNull(getResposta);
            Assert.Equal(3, getResposta.Oferta.Count);
            Assert.Equal(contatoResposta.Oferta.Select(o => o.Id), getResposta.Oferta.Select(o => o.Id));

            using var clientCorretor = fixture.Factory.CreateClient();
            clientCorretor.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={corretorToken}");

            using var respMetricasAntes = await clientCorretor.GetAsync("/api/painel/metricas?dias=30");
            Assert.Equal(HttpStatusCode.OK, respMetricasAntes.StatusCode);
            var metricasAntes = await respMetricasAntes.Content.ReadFromJsonAsync<MetricasPainelResponse>();
            Assert.NotNull(metricasAntes);
            var confirmadosAntes = metricasAntes.HorariosConfirmados;

            var slotEscolhidoId = slot1.Id;
            using var respAgendamento = await clientPublico.PostAsJsonAsync(
                $"/conversas/{conversaId:D}/agendamentos",
                new AgendamentoRequest(slotEscolhidoId));
            Assert.Equal(HttpStatusCode.OK, respAgendamento.StatusCode);

            var jsonAgendamento = await respAgendamento.Content.ReadAsStringAsync();
            Assert.Contains("\"estado\":\"confirmado\"", jsonAgendamento);
            Assert.Contains("\"horario\":", jsonAgendamento);
            Assert.Contains("\"alternativas\":[]", jsonAgendamento);

            var agendamentoResp = JsonSerializer.Deserialize<AgendamentoDaConversa>(
                jsonAgendamento,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.NotNull(agendamentoResp);
            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamentoResp.Estado);
            Assert.NotNull(agendamentoResp.Horario);
            Assert.Equal(slotEscolhidoId, agendamentoResp.Horario.Id);
            Assert.Equal(slot1.Inicio, agendamentoResp.Horario.Inicio);
            Assert.Equal(slot1.Fim, agendamentoResp.Horario.Fim);

            using var respGetApos = await clientPublico.GetAsync($"/conversas/{conversaId:D}");
            Assert.Equal(HttpStatusCode.OK, respGetApos.StatusCode);
            var getApos = await respGetApos.Content.ReadFromJsonAsync<ConversaResponse>();
            Assert.NotNull(getApos);
            Assert.Empty(getApos.Oferta);
            Assert.Equal(4, getApos.Mensagens.Count);

            var msgLead = getApos.Mensagens[^2];
            var msgLia = getApos.Mensagens[^1];
            Assert.Equal(Papeis.Lead, msgLead.Papel);
            Assert.Equal(Papeis.Agente, msgLia.Papel);
            Assert.NotNull(msgLia.Agendamento);
            Assert.Equal(EstadosDoAgendamento.Confirmado, msgLia.Agendamento.Estado);
            Assert.Equal(slotEscolhidoId, msgLia.Agendamento.Horario!.Id);

            using var respMetricasDepois = await clientCorretor.GetAsync("/api/painel/metricas?dias=30");
            Assert.Equal(HttpStatusCode.OK, respMetricasDepois.StatusCode);
            var metricasDepois = await respMetricasDepois.Content.ReadFromJsonAsync<MetricasPainelResponse>();
            Assert.NotNull(metricasDepois);
            Assert.Equal(confirmadosAntes + 1, metricasDepois.HorariosConfirmados);

            using var respDetalhe = await clientCorretor.GetAsync($"/api/painel/leads/{leadId:D}");
            Assert.Equal(HttpStatusCode.OK, respDetalhe.StatusCode);
            var detalhe = await respDetalhe.Content.ReadFromJsonAsync<DetalheLeadPainelResponse>();
            Assert.NotNull(detalhe);
            Assert.NotNull(detalhe.Agendamento);
            Assert.Equal(slot1.Inicio, detalhe.Agendamento.DataHora);
            Assert.Equal(EstadosDoAgendamento.Confirmado, detalhe.Agendamento.Status);
            Assert.Equal(slot1.Fim, detalhe.Agendamento.Fim);
            Assert.NotNull(detalhe.Encaminhamento);
            Assert.Equal(corretor.Id, detalhe.Encaminhamento.Corretor!.Id);
            Assert.Equal(corretor.Nome, detalhe.Encaminhamento.Corretor.Nome);
            Assert.Contains(detalhe.Transcricao, t => t.Texto.Contains($"Combinado! {corretor.Nome} vai te chamar no contato que você deixou."));
            Assert.Contains(detalhe.Transcricao, t => t.Texto == msgLead.Texto);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var cVerif = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
                Assert.Equal(intencaoEm, cVerif.IntencaoEm);
                Assert.Equal(essenciaisEm, cVerif.EssenciaisEm);
                Assert.Equal(encaminhadaEm, cVerif.EncaminhadaEm);
                Assert.Equal(corretorAtribuidoEm, cVerif.CorretorAtribuidoEm);
                Assert.Equal(tentativasReengajamento, cVerif.TentativasReengajamento);
                Assert.Equal(1, await db.Encaminhamentos.CountAsync(e => e.ConversaId == conversaId));
            }

            Assert.Equal(0, fixture.AgenteHandler.Chamadas);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await db.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            await db.Sessoes.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Http_409_horario_perdido_expoe_oferta_no_nivel_raiz()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();
        Guid lead1Id, lead2Id, outroLead1Id, outroLead2Id;
        var corretor1 = CriarCorretor(agora, "CorretorComAlt");
        var corretor2 = CriarCorretor(agora, "CorretorSemAlt");

        var slotReservado1 = Slot.Novo(corretor1.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slotAlt1 = Slot.Novo(corretor1.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));
        var slotAlt2 = Slot.Novo(corretor1.Id, agora.AddDays(4), agora.AddDays(4).AddHours(1));

        var slotReservado2 = Slot.Novo(corretor2.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var outro1 = Lead.Novo(agora);
            var outro2 = Lead.Novo(agora);
            outroLead1Id = outro1.Id;
            outroLead2Id = outro2.Id;

            db.Leads.AddRange(outro1, outro2);
            db.Corretores.AddRange(corretor1, corretor2);
            db.Slots.AddRange(slotReservado1, slotAlt1, slotAlt2, slotReservado2);
            await db.SaveChangesAsync();

            await db.Slots.Where(s => s.Id == slotReservado1.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, outro1.Id));
            await db.Slots.Where(s => s.Id == slotReservado2.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, outro2.Id));

            var repo = new ConversaRepositorio(db);

            var c1 = await repo.ObterOuCriarAsync(conversa1Id, agora, default);
            lead1Id = c1.LeadId;
            c1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c1.Lead.RegistrarContato("Lead 1", "11988880002", null, agora);

            var c2 = await repo.ObterOuCriarAsync(conversa2Id, agora, default);
            lead2Id = c2.LeadId;
            c2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c2.Lead.RegistrarContato("Lead 2", "11988880003", null, agora);

            db.Encaminhamentos.AddRange(
                Encaminhamento.Novo(c1.Id, lead1Id, corretor1.Id, corretor1.Especialidade, agora),
                Encaminhamento.Novo(c2.Id, lead2Id, corretor2.Id, corretor2.Especialidade, agora));

            await db.SaveChangesAsync();
        }

        try
        {
            using var client = fixture.Factory.CreateClient();

            using var resp1 = await client.PostAsJsonAsync(
                $"/conversas/{conversa1Id:D}/agendamentos",
                new AgendamentoRequest(slotReservado1.Id));
            Assert.Equal(HttpStatusCode.Conflict, resp1.StatusCode);

            var json1 = await resp1.Content.ReadAsStringAsync();
            using var doc1 = JsonDocument.Parse(json1);
            var root1 = doc1.RootElement;
            Assert.Equal("about:blank", root1.GetProperty("type").GetString());
            Assert.Equal(409, root1.GetProperty("status").GetInt32());
            Assert.Equal("horario indisponivel", root1.GetProperty("title").GetString());
            Assert.Equal("horario_indisponivel", root1.GetProperty("codigo").GetString());

            Assert.False(root1.TryGetProperty("estado", out _));
            Assert.False(root1.TryGetProperty("horario", out _));

            var oferta1 = root1.GetProperty("oferta");
            Assert.Equal(JsonValueKind.Array, oferta1.ValueKind);
            Assert.Equal(2, oferta1.GetArrayLength());
            Assert.Equal(slotAlt1.Id, oferta1[0].GetProperty("id").GetInt64());
            Assert.Equal(slotAlt2.Id, oferta1[1].GetProperty("id").GetInt64());

            using var resp2 = await client.PostAsJsonAsync(
                $"/conversas/{conversa2Id:D}/agendamentos",
                new AgendamentoRequest(slotReservado2.Id));
            Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);

            var json2 = await resp2.Content.ReadAsStringAsync();
            using var doc2 = JsonDocument.Parse(json2);
            var root2 = doc2.RootElement;
            Assert.Equal("about:blank", root2.GetProperty("type").GetString());
            Assert.Equal(409, root2.GetProperty("status").GetInt32());
            Assert.Equal("horario indisponivel", root2.GetProperty("title").GetString());
            Assert.Equal("horario_indisponivel", root2.GetProperty("codigo").GetString());

            var oferta2 = root2.GetProperty("oferta");
            Assert.Equal(JsonValueKind.Array, oferta2.ValueKind);
            Assert.Equal(0, oferta2.GetArrayLength());

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();

                var s1 = await db.Slots.SingleAsync(s => s.Id == slotReservado1.Id);
                Assert.Equal(outroLead1Id, s1.LeadId);

                var s2 = await db.Slots.SingleAsync(s => s.Id == slotReservado2.Id);
                Assert.Equal(outroLead2Id, s2.LeadId);

                Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversa1Id));
                Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversa2Id));
            }
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            var corretorIds = new[] { corretor1.Id, corretor2.Id };
            var conversaIds = new[] { conversa1Id, conversa2Id };
            var leadIds = new[] { lead1Id, lead2Id, outroLead1Id, outroLead2Id };

            await db.Slots.Where(s => corretorIds.Contains(s.CorretorId)).ExecuteDeleteAsync();
            await db.Mensagens.Where(m => conversaIds.Contains(m.ConversaId)).ExecuteDeleteAsync();
            await db.Encaminhamentos.Where(e => conversaIds.Contains(e.ConversaId)).ExecuteDeleteAsync();
            await db.Conversas.Where(c => conversaIds.Contains(c.Id)).ExecuteDeleteAsync();
            await db.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await db.Corretores.Where(c => corretorIds.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"slotId\":0}")]
    [InlineData("{\"slotId\":-1}")]
    [InlineData("{\"slotId\":null}")]
    [InlineData("{\"slotId\":1,\"extra\":true}")]
    public async Task Http_request_invalido_retorna_400_sem_efeitos(string corpoJson)
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora, "CorretorInvalido");
        var slot = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.Add(corretor);
            db.Slots.Add(slot);

            var repo = new ConversaRepositorio(db);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Invalido", "11988880004", null, agora);

            db.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await db.SaveChangesAsync();
        }

        try
        {
            using var client = fixture.Factory.CreateClient();
            using var conteudo = new StringContent(corpoJson, Encoding.UTF8, "application/json");

            using var resposta = await client.PostAsync($"/conversas/{conversaId:D}/agendamentos", conteudo);
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var slotVerif = await db.Slots.SingleAsync(s => s.Id == slot.Id);
                Assert.Null(slotVerif.LeadId);

                var totalMensagens = await db.Mensagens.CountAsync(m => m.ConversaId == conversaId);
                Assert.Equal(0, totalMensagens);
            }

            Assert.Equal(0, fixture.AgenteHandler.Chamadas);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await db.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            await db.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Http_conversa_de_outra_conta_retorna_404_sem_efeitos()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var contaDona = CriarCliente(agora, "ClienteDona");
        var outraConta = CriarCliente(agora, "ClienteOutro");
        var corretor = CriarCorretor(agora, "Corretor404");
        var outraToken = TokenSeguro.Criar();

        var slot = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.AddRange(contaDona, outraConta, corretor);
            db.Sessoes.Add(SessaoCorretor.Nova(outraConta.Id, TokenSeguro.Sha256(outraToken), agora.AddDays(1)));
            db.Slots.Add(slot);

            var repo = new ConversaRepositorio(db);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Dona", "11988880005", null, agora);
            conversa.VincularConta(contaDona.Id);

            db.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await db.SaveChangesAsync();
        }

        try
        {
            using var client = fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={outraToken}");

            using var resposta = await client.PostAsJsonAsync(
                $"/conversas/{conversaId:D}/agendamentos",
                new AgendamentoRequest(slot.Id));
            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var slotVerif = await db.Slots.SingleAsync(s => s.Id == slot.Id);
                Assert.Null(slotVerif.LeadId);

                var totalMensagens = await db.Mensagens.CountAsync(m => m.ConversaId == conversaId);
                Assert.Equal(0, totalMensagens);
            }

            Assert.Equal(0, fixture.AgenteHandler.Chamadas);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await db.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await db.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            var ids = new[] { contaDona.Id, outraConta.Id, corretor.Id };
            await db.Sessoes.Where(s => ids.Contains(s.CorretorId)).ExecuteDeleteAsync();
            await db.Corretores.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Http_painel_expoe_fim_real_utc_com_duracao_diferente_de_uma_hora_e_nulo_quando_sem_reserva()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId1 = Guid.NewGuid();
        var conversaId2 = Guid.NewGuid();
        Guid leadId1;
        Guid leadId2;
        var corretor = CriarCorretor(agora, "CorretorFimReal");
        var corretorToken = TokenSeguro.Criar();

        var fusoSp = TimeSpan.FromHours(-3);
        var agoraSp = agora.ToOffset(fusoSp);
        var inicioBase = new DateTimeOffset(agoraSp.Year, agoraSp.Month, agoraSp.Day, 15, 0, 0, fusoSp).ToUniversalTime().AddDays(2);

        var slot90Min = Slot.Novo(corretor.Id, inicioBase, inicioBase.AddMinutes(90));
        var slotExtra1 = Slot.Novo(corretor.Id, inicioBase.AddDays(1), inicioBase.AddDays(1).AddHours(1));
        var slotExtra2 = Slot.Novo(corretor.Id, inicioBase.AddDays(2), inicioBase.AddDays(2).AddHours(1));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            db.Corretores.Add(corretor);
            db.Sessoes.Add(SessaoCorretor.Nova(corretor.Id, TokenSeguro.Sha256(corretorToken), agora.AddDays(1)));
            db.Slots.AddRange(slot90Min, slotExtra1, slotExtra2);

            var repo = new ConversaRepositorio(db);

            var conversa1 = await repo.ObterOuCriarAsync(conversaId1, agora.AddHours(-1), default);
            leadId1 = conversa1.LeadId;
            conversa1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-1));
            conversa1.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 90Min", PrecoMax: 600000), agora.AddHours(-1));

            var turnoHandoff1 = new TurnoResponse(
                "Encaminhando lead 1...",
                Intencoes.Compra,
                new CamposExtraidos(),
                ProximasAcoes.DirecionarEspecialista,
                [],
                null,
                EssenciaisCompletos: true);
            conversa1.RegistrarTurno("Quero agendar visita", turnoHandoff1, agora.AddHours(-1));
            conversa1.RegistrarCorretorAtribuido(agora.AddHours(-1));
            db.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa1.Id, leadId1, corretor.Id, corretor.Especialidade, agora.AddHours(-1)));

            var conversa2 = await repo.ObterOuCriarAsync(conversaId2, agora.AddHours(-1), default);
            leadId2 = conversa2.LeadId;
            conversa2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-1));
            conversa2.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead Sem Agendamento", PrecoMax: 400000), agora.AddHours(-1));

            var turnoHandoff2 = new TurnoResponse(
                "Encaminhando lead 2...",
                Intencoes.Compra,
                new CamposExtraidos(),
                ProximasAcoes.DirecionarEspecialista,
                [],
                null,
                EssenciaisCompletos: true);
            conversa2.RegistrarTurno("Quero informacoes", turnoHandoff2, agora.AddHours(-1));
            conversa2.RegistrarCorretorAtribuido(agora.AddHours(-1));
            db.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa2.Id, leadId2, corretor.Id, corretor.Especialidade, agora.AddHours(-1)));

            await db.SaveChangesAsync();
        }

        try
        {
            using var clientPublico = fixture.Factory.CreateClient();
            using var clientCorretor = fixture.Factory.CreateClient();
            clientCorretor.DefaultRequestHeaders.Add("Cookie", $"{CorretorAuthenticationDefaults.CookieName}={corretorToken}");

            using var respAntes = await clientCorretor.GetAsync($"/api/painel/leads/{leadId1:D}");
            Assert.Equal(HttpStatusCode.OK, respAntes.StatusCode);
            var jsonAntes = await respAntes.Content.ReadAsStringAsync();
            using (var docAntes = JsonDocument.Parse(jsonAntes))
            {
                Assert.Equal(JsonValueKind.Null, docAntes.RootElement.GetProperty("agendamento").ValueKind);
            }

            using var respContato = await clientPublico.PostAsJsonAsync(
                $"/conversas/{conversaId1:D}/contato",
                new ContatoRequest("Lead 90Min", "11999990090", "lead90@teste.local"));
            Assert.Equal(HttpStatusCode.OK, respContato.StatusCode);

            using var respAgendamento = await clientPublico.PostAsJsonAsync(
                $"/conversas/{conversaId1:D}/agendamentos",
                new AgendamentoRequest(slot90Min.Id));
            Assert.Equal(HttpStatusCode.OK, respAgendamento.StatusCode);

            var jsonAgendamento = await respAgendamento.Content.ReadAsStringAsync();
            var agendamentoResp = JsonSerializer.Deserialize<AgendamentoDaConversa>(
                jsonAgendamento,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.NotNull(agendamentoResp);
            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamentoResp.Estado);
            Assert.NotNull(agendamentoResp.Horario);
            Assert.Equal(slot90Min.Id, agendamentoResp.Horario.Id);
            Assert.Equal(slot90Min.Inicio, agendamentoResp.Horario.Inicio);
            Assert.Equal(slot90Min.Fim, agendamentoResp.Horario.Fim);

            using var respDetalhe = await clientCorretor.GetAsync($"/api/painel/leads/{leadId1:D}");
            Assert.Equal(HttpStatusCode.OK, respDetalhe.StatusCode);
            var jsonDetalhe = await respDetalhe.Content.ReadAsStringAsync();

            using var docDetalhe = JsonDocument.Parse(jsonDetalhe);
            var agendamentoEl = docDetalhe.RootElement.GetProperty("agendamento");
            Assert.Equal(JsonValueKind.Object, agendamentoEl.ValueKind);

            Assert.True(agendamentoEl.TryGetProperty("dataHora", out var propDataHora));
            Assert.True(agendamentoEl.TryGetProperty("status", out var propStatus));
            Assert.Equal(EstadosDoAgendamento.Confirmado, propStatus.GetString());

            Assert.True(agendamentoEl.TryGetProperty("fim", out var propFim));

            var fimStr = propFim.GetString();
            Assert.NotNull(fimStr);
            var fimUtc = DateTimeOffset.Parse(fimStr);
            Assert.Equal(TimeSpan.Zero, fimUtc.Offset);
            Assert.Equal(slot90Min.Fim, fimUtc);
            Assert.NotEqual(slot90Min.Inicio.AddHours(1), fimUtc);
            Assert.Equal(TimeSpan.FromMinutes(90), fimUtc - slot90Min.Inicio);

            var dataHoraUtc = DateTimeOffset.Parse(propDataHora.GetString()!);
            Assert.Equal(TimeSpan.Zero, dataHoraUtc.Offset);
            Assert.Equal(slot90Min.Inicio, dataHoraUtc);

            var detalheDto = JsonSerializer.Deserialize<DetalheLeadPainelResponse>(
                jsonDetalhe,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.NotNull(detalheDto);
            Assert.NotNull(detalheDto.Agendamento);
            Assert.Equal(slot90Min.Inicio, detalheDto.Agendamento.DataHora);
            Assert.Equal(EstadosDoAgendamento.Confirmado, detalheDto.Agendamento.Status);
            Assert.Equal(slot90Min.Fim, detalheDto.Agendamento.Fim);
            Assert.NotEqual(detalheDto.Agendamento.DataHora.AddHours(1), detalheDto.Agendamento.Fim);

            using var respLeadSemReserva = await clientCorretor.GetAsync($"/api/painel/leads/{leadId2:D}");
            Assert.Equal(HttpStatusCode.OK, respLeadSemReserva.StatusCode);
            var jsonLeadSemReserva = await respLeadSemReserva.Content.ReadAsStringAsync();
            using var docLeadSemReserva = JsonDocument.Parse(jsonLeadSemReserva);
            Assert.Equal(JsonValueKind.Null, docLeadSemReserva.RootElement.GetProperty("agendamento").ValueKind);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                var slotNoBanco = await db.Slots.AsNoTracking().SingleAsync(s => s.Id == slot90Min.Id);
                Assert.Equal(leadId1, slotNoBanco.LeadId);
                Assert.Equal(slot90Min.Inicio, slotNoBanco.Inicio);
                Assert.Equal(slot90Min.Fim, slotNoBanco.Fim);
                Assert.Equal(TimeSpan.FromMinutes(90), slotNoBanco.Fim - slotNoBanco.Inicio);
                Assert.NotEqual(slotNoBanco.Inicio.AddHours(1), slotNoBanco.Fim);
            }

            Assert.Equal(0, fixture.AgenteHandler.Chamadas);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Encaminhamentos.Where(e => e.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Conversas.Where(c => c.Id == conversaId1 || c.Id == conversaId2).ExecuteDeleteAsync();
            await db.Leads.Where(l => l.Id == leadId1 || l.Id == leadId2).ExecuteDeleteAsync();
            await db.Sessoes.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await db.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    private static Corretor CriarCorretor(DateTimeOffset agora, string prefixo)
    {
        var id = Guid.NewGuid();
        var corretor = Corretor.NovaConta(
            nome: $"{prefixo} {id:N}",
            email: $"{prefixo.ToLowerInvariant()}-{id:N}@teste.com",
            emailNormalizado: $"{prefixo.ToUpperInvariant()}-{id:N}@TESTE.COM",
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

    private static Corretor CriarCliente(DateTimeOffset agora, string prefixo)
    {
        var id = Guid.NewGuid();
        var cliente = Corretor.NovaConta(
            nome: $"{prefixo} {id:N}",
            email: $"{prefixo.ToLowerInvariant()}-{id:N}@teste.com",
            emailNormalizado: $"{prefixo.ToUpperInvariant()}-{id:N}@TESTE.COM",
            telefone: "11988888888",
            senhaHash: "fake-hash",
            perfil: PerfisDoPainel.Cliente,
            regioes: ["Sul"],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: "v1",
            em: agora);
        cliente.Aprovar(agora);
        return cliente;
    }
}

public sealed class HttpAgendamentoFixture : IAsyncLifetime
{
    private readonly string schema = "s47_agendamento_" + Guid.NewGuid().ToString("N");
    private string conexaoBase = string.Empty;
    private readonly string? conexaoAnterior =
        Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

    public HttpAgendamentoApiFactory Factory { get; private set; } = null!;
    public AgenteContadorHandler AgenteHandler { get; } = new();

    public async Task InitializeAsync()
    {
        conexaoBase = PostgresTestDatabase.ObterConexaoParaAplicacao();
        await using var admin = new NpgsqlConnection(conexaoBase);
        await admin.OpenAsync();
        await using var criar = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
        await criar.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(conexaoBase) { SearchPath = schema };
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", builder.ConnectionString);

        Factory = new HttpAgendamentoApiFactory(builder.ConnectionString, schema, AgenteHandler);

        using var clientBoot = Factory.CreateClient();

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
        await db.RegistroMetricas.Where(r => r.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, DateTimeOffset.UtcNow.AddDays(-400)));
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Factory is not null)
            {
                await Factory.DisposeAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", conexaoAnterior);
            if (!string.IsNullOrEmpty(conexaoBase))
            {
                await using var admin = new NpgsqlConnection(conexaoBase);
                await admin.OpenAsync();
                await using var remover = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
                await remover.ExecuteNonQueryAsync();
            }
        }
    }
}

public sealed class HttpAgendamentoApiFactory(
    string conexao,
    string schema,
    AgenteContadorHandler agenteHandler) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SolarDbContext>();
            services.AddScoped(_ => new SolarDbContext(
                new DbContextOptionsBuilder<SolarDbContext>()
                    .UseNpgsql(conexao, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                    .Options));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider.System);

            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                (s.ImplementationType == typeof(ServicoDeExpurgo) ||
                 s.ImplementationType == typeof(ServicoDeReengajamento))).ToArray())
            {
                services.Remove(service);
            }

            services.RemoveAll<AgenteClient>();
            services.AddScoped(_ =>
            {
                var http = new HttpClient(agenteHandler)
                {
                    BaseAddress = new Uri("http://agente.test")
                };
                return new AgenteClient(http, NullLogger<AgenteClient>.Instance);
            });
        });
    }
}

public sealed class AgenteContadorHandler : HttpMessageHandler
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
