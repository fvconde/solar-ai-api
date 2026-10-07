using System.Globalization;
using System.Security.Claims;
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
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class AgendamentoPorBotaoPostgresTeste
{
    [Fact]
    public async Task Sucesso_200_reserva_e_duas_mensagens_corretas()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        var fusoSaoPaulo = TimeSpan.FromHours(-3);
        var agoraSp = agora.ToOffset(fusoSaoPaulo);
        var dataAlvoSp = agoraSp.Date.AddDays(2);
        var inicioSaoPaulo = new DateTimeOffset(dataAlvoSp.Year, dataAlvoSp.Month, dataAlvoSp.Day, 14, 0, 0, fusoSaoPaulo);
        var slot = Slot.Novo(corretor.Id, inicioSaoPaulo.ToUniversalTime(), inicioSaoPaulo.AddHours(1).ToUniversalTime());

        var cultura = new CultureInfo("pt-BR");
        var nomeDiaSemana = inicioSaoPaulo.ToString("dddd", cultura);
        nomeDiaSemana = nomeDiaSemana.Replace("-feira", "", StringComparison.OrdinalIgnoreCase);
        nomeDiaSemana = char.ToUpper(nomeDiaSemana[0], cultura) + nomeDiaSemana[1..];
        var nomeMes = inicioSaoPaulo.ToString("MMMM", cultura);
        var textoEsperadoLead = $"{nomeDiaSemana}, {inicioSaoPaulo.Day} de {nomeMes} às 14h";

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Titular", "11999998888", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resultado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slot.Id), default);

            var ok = Assert.IsType<OkObjectResult>(resultado.Result);
            var agendamento = Assert.IsType<AgendamentoDaConversa>(ok.Value);

            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamento.Estado);
            Assert.NotNull(agendamento.Horario);
            Assert.Equal(slot.Id, agendamento.Horario.Id);
            Assert.Equal(slot.Inicio, agendamento.Horario.Inicio);
            Assert.Equal(slot.Fim, agendamento.Horario.Fim);
            Assert.Empty(agendamento.Alternativas);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotSalvo = await verificacao.Slots.SingleAsync(s => s.Id == slot.Id);
            Assert.Equal(leadId, slotSalvo.LeadId);

            var mensagens = await verificacao.Mensagens
                .Where(m => m.ConversaId == conversaId)
                .OrderBy(m => m.Id)
                .ToListAsync();

            Assert.Equal(2, mensagens.Count);

            var msgLead = mensagens[0];
            Assert.Equal(Papeis.Lead, msgLead.Papel);
            Assert.Equal(textoEsperadoLead, msgLead.Texto);
            Assert.Contains("às 14h", msgLead.Texto);
            Assert.Contains(nomeMes, msgLead.Texto);
            Assert.Contains(inicioSaoPaulo.Day.ToString(), msgLead.Texto);
            Assert.StartsWith(nomeDiaSemana, msgLead.Texto);
            Assert.Null(msgLead.StatusAgendamento);
            Assert.Null(msgLead.SlotId);

            var msgLia = mensagens[1];
            Assert.Equal(Papeis.Agente, msgLia.Papel);
            Assert.Equal($"Combinado! {corretor.Nome} vai te chamar no contato que você forneceu no horário agendado.", msgLia.Texto);
            Assert.Equal(ProximasAcoes.AgendarReuniao, msgLia.ProximaAcao);
            Assert.Equal(EstadosDoAgendamento.Confirmado, msgLia.StatusAgendamento);
            Assert.Equal(slot.Id, msgLia.SlotId);

            var resGet = await controller.Obter(conversaId, default);
            var okGet = Assert.IsType<OkObjectResult>(resGet.Result);
            var conversaResponse = Assert.IsType<ConversaResponse>(okGet.Value);

            Assert.Equal(2, conversaResponse.Mensagens.Count);
            Assert.Equal(msgLead.Texto, conversaResponse.Mensagens[0].Texto);
            Assert.Equal(msgLia.Texto, conversaResponse.Mensagens[1].Texto);
            Assert.NotNull(conversaResponse.Mensagens[1].Agendamento);
            Assert.Equal(EstadosDoAgendamento.Confirmado, conversaResponse.Mensagens[1].Agendamento!.Estado);
            Assert.Empty(conversaResponse.Oferta);
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
    public async Task Reserva_slot_fora_dos_tres_primeiros_oferecidos_retorna_200()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);

        var slot1 = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        var slot2 = Slot.Novo(corretor.Id, agora.AddDays(2), agora.AddDays(2).AddHours(1));
        var slot3 = Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));
        var slot4 = Slot.Novo(corretor.Id, agora.AddDays(4), agora.AddDays(4).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slot1, slot2, slot3, slot4);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Teste", "11999991111", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resultado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slot4.Id), default);

            var ok = Assert.IsType<OkObjectResult>(resultado.Result);
            var agendamento = Assert.IsType<AgendamentoDaConversa>(ok.Value);
            Assert.Equal(EstadosDoAgendamento.Confirmado, agendamento.Estado);
            Assert.Equal(slot4.Id, agendamento.Horario!.Id);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotSalvo = await verificacao.Slots.SingleAsync(s => s.Id == slot4.Id);
            Assert.Equal(leadId, slotSalvo.LeadId);
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
    public async Task Slot_ja_reservado_e_slot_vencido_retornam_409_com_oferta()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var outroLead = Lead.Novo(agora);
        var corretor = CriarCorretor(agora);

        var slotReservado = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        var slotVencido = Slot.Novo(corretor.Id, agora.AddDays(-2), agora.AddDays(-2).AddHours(1));
        var slotAlternativa = Slot.Novo(corretor.Id, agora.AddDays(3), agora.AddDays(3).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Leads.Add(outroLead);
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slotReservado, slotVencido, slotAlternativa);
            await preparacao.SaveChangesAsync();

            await preparacao.Slots
                .Where(s => s.Id == slotReservado.Id && s.LeadId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, outroLead.Id));

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Teste", "11999992222", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resReservado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slotReservado.Id), default);
            var objReservado = Assert.IsType<ObjectResult>(resReservado.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objReservado.StatusCode);
            var problemReservado = Assert.IsType<ProblemDetails>(objReservado.Value);
            Assert.Equal("horario indisponivel", problemReservado.Title);
            Assert.Equal("about:blank", problemReservado.Type);
            Assert.Equal("horario_indisponivel", problemReservado.Extensions["codigo"]);
            var ofertaReservado = Assert.IsAssignableFrom<IReadOnlyList<SlotOferecido>>(problemReservado.Extensions["oferta"]);
            Assert.Single(ofertaReservado);
            Assert.Equal(slotAlternativa.Id, ofertaReservado[0].Id);

            var resVencido = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slotVencido.Id), default);
            var objVencido = Assert.IsType<ObjectResult>(resVencido.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objVencido.StatusCode);
            var problemVencido = Assert.IsType<ProblemDetails>(objVencido.Value);
            Assert.Equal("horario indisponivel", problemVencido.Title);
            Assert.Equal("about:blank", problemVencido.Type);
            Assert.Equal(StatusCodes.Status409Conflict, problemVencido.Status);
            Assert.Equal("horario_indisponivel", problemVencido.Extensions["codigo"]);
            var ofertaVencido = Assert.IsAssignableFrom<IReadOnlyList<SlotOferecido>>(problemVencido.Extensions["oferta"]);
            Assert.Single(ofertaVencido);
            Assert.Equal(slotAlternativa.Id, ofertaVencido[0].Id);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotReservadoVerif = await verificacao.Slots.SingleAsync(s => s.Id == slotReservado.Id);
            Assert.Equal(outroLead.Id, slotReservadoVerif.LeadId);

            var totalMensagens = await verificacao.Mensagens.CountAsync(m => m.ConversaId == conversaId);
            Assert.Equal(0, totalMensagens);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            var leadIds = new[] { leadId, outroLead.Id };
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Slot_indisponivel_sem_alternativas_retorna_409_com_oferta_vazia()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var outroLead = Lead.Novo(agora);
        var corretor = CriarCorretor(agora);

        var slotReservado = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        var slotVencido = Slot.Novo(corretor.Id, agora.AddDays(-2), agora.AddDays(-2).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Leads.Add(outroLead);
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.AddRange(slotReservado, slotVencido);
            await preparacao.SaveChangesAsync();

            await preparacao.Slots
                .Where(s => s.Id == slotReservado.Id && s.LeadId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeadId, outroLead.Id));

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Sem Alternativas", "11999990000", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resReservado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slotReservado.Id), default);
            var objReservado = Assert.IsType<ObjectResult>(resReservado.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objReservado.StatusCode);
            var problemReservado = Assert.IsType<ProblemDetails>(objReservado.Value);
            Assert.Equal("horario indisponivel", problemReservado.Title);
            Assert.Equal("about:blank", problemReservado.Type);
            Assert.Equal(StatusCodes.Status409Conflict, problemReservado.Status);
            Assert.Equal("horario_indisponivel", problemReservado.Extensions["codigo"]);
            var ofertaReservado = Assert.IsAssignableFrom<IReadOnlyList<SlotOferecido>>(problemReservado.Extensions["oferta"]);
            Assert.NotNull(ofertaReservado);
            Assert.Empty(ofertaReservado);

            var resVencido = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slotVencido.Id), default);
            var objVencido = Assert.IsType<ObjectResult>(resVencido.Result);
            Assert.Equal(StatusCodes.Status409Conflict, objVencido.StatusCode);
            var problemVencido = Assert.IsType<ProblemDetails>(objVencido.Value);
            Assert.Equal("horario indisponivel", problemVencido.Title);
            Assert.Equal("about:blank", problemVencido.Type);
            Assert.Equal(StatusCodes.Status409Conflict, problemVencido.Status);
            Assert.Equal("horario_indisponivel", problemVencido.Extensions["codigo"]);
            var ofertaVencido = Assert.IsAssignableFrom<IReadOnlyList<SlotOferecido>>(problemVencido.Extensions["oferta"]);
            Assert.NotNull(ofertaVencido);
            Assert.Empty(ofertaVencido);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotReservadoVerif = await verificacao.Slots.SingleAsync(s => s.Id == slotReservado.Id);
            Assert.Equal(outroLead.Id, slotReservadoVerif.LeadId);

            var slotVencidoVerif = await verificacao.Slots.SingleAsync(s => s.Id == slotVencido.Id);
            Assert.Null(slotVencidoVerif.LeadId);

            var totalMensagens = await verificacao.Mensagens.CountAsync(m => m.ConversaId == conversaId);
            Assert.Equal(0, totalMensagens);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            var leadIds = new[] { leadId, outroLead.Id };
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Agendamento_ja_confirmado_retorna_409_sem_oferta()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);
        var slot = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Teste", "11999993333", null, agora);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            var msgConfirmada = Mensagem.DaLia(conversa.Id, "Reuniao marcada", ProximasAcoes.AgendarReuniao, agora);
            msgConfirmada.RegistrarAgendamento(EstadosDoAgendamento.Confirmado, null);
            preparacao.Mensagens.Add(msgConfirmada);

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resultado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slot.Id), default);

            var obj = Assert.IsType<ObjectResult>(resultado.Result);
            Assert.Equal(StatusCodes.Status409Conflict, obj.StatusCode);
            var problem = Assert.IsType<ProblemDetails>(obj.Value);
            Assert.Equal("agendamento ja confirmado", problem.Title);
            Assert.Equal("agendamento_ja_confirmado", problem.Extensions["codigo"]);
            Assert.False(problem.Extensions.ContainsKey("oferta"));

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotVerif = await verificacao.Slots.SingleAsync(s => s.Id == slot.Id);
            Assert.Null(slotVerif.LeadId);

            var totalMensagens = await verificacao.Mensagens.CountAsync(m => m.ConversaId == conversaId);
            Assert.Equal(1, totalMensagens);
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
    public async Task Sem_contato_e_sem_corretor_retornam_respectivos_409()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaSemContatoId = Guid.NewGuid();
        var conversaSemCorretorId = Guid.NewGuid();
        Guid lead1, lead2;
        var corretor = CriarCorretor(agora);
        var slot = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);

            var c1 = await repo.ObterOuCriarAsync(conversaSemContatoId, agora, default);
            lead1 = c1.LeadId;
            c1.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c1.Id, lead1, corretor.Id, corretor.Especialidade, agora));

            var c2 = await repo.ObterOuCriarAsync(conversaSemCorretorId, agora, default);
            lead2 = c2.LeadId;
            c2.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c2.Lead.RegistrarContato("Lead 2", "11999994444", null, agora);

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resSemContato = await controller.RegistrarAgendamento(
                conversaSemContatoId, new AgendamentoRequest(slot.Id), default);
            var obj1 = Assert.IsType<ObjectResult>(resSemContato.Result);
            Assert.Equal(StatusCodes.Status409Conflict, obj1.StatusCode);
            var prob1 = Assert.IsType<ProblemDetails>(obj1.Value);
            Assert.Equal("contato pendente", prob1.Title);
            Assert.Equal("contato_pendente", prob1.Extensions["codigo"]);
            Assert.False(prob1.Extensions.ContainsKey("oferta"));

            var resSemCorretor = await controller.RegistrarAgendamento(
                conversaSemCorretorId, new AgendamentoRequest(slot.Id), default);
            var obj2 = Assert.IsType<ObjectResult>(resSemCorretor.Result);
            Assert.Equal(StatusCodes.Status409Conflict, obj2.StatusCode);
            var prob2 = Assert.IsType<ProblemDetails>(obj2.Value);
            Assert.Equal("corretor nao atribuido", prob2.Title);
            Assert.Equal("corretor_nao_atribuido", prob2.Extensions["codigo"]);
            Assert.False(prob2.Extensions.ContainsKey("oferta"));
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            var ids = new[] { conversaSemContatoId, conversaSemCorretorId };
            var leadIds = new[] { lead1, lead2 };
            await limpeza.Encaminhamentos.Where(e => ids.Contains(e.ConversaId)).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => leadIds.Contains(l.Id)).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Casos_404_para_slot_e_conversa()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretorA = CriarCorretor(agora);
        var corretorB = CriarCorretor(agora);
        var slotB = Slot.Novo(corretorB.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.AddRange(corretorA, corretorB);
            preparacao.Slots.Add(slotB);

            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c.Lead.RegistrarContato("Lead", "11999995555", null, agora);
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c.Id, leadId, corretorA.Id, corretorA.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var resInexistente = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(999999L), default);
            var obj1 = Assert.IsAssignableFrom<ObjectResult>(resInexistente.Result);
            Assert.Equal(StatusCodes.Status404NotFound, obj1.StatusCode);

            var resOutroCorretor = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slotB.Id), default);
            var obj2 = Assert.IsAssignableFrom<ObjectResult>(resOutroCorretor.Result);
            Assert.Equal(StatusCodes.Status404NotFound, obj2.StatusCode);

            var resConversaInexistente = await controller.RegistrarAgendamento(
                Guid.NewGuid(), new AgendamentoRequest(slotB.Id), default);
            var obj3 = Assert.IsAssignableFrom<ObjectResult>(resConversaInexistente.Result);
            Assert.Equal(StatusCodes.Status404NotFound, obj3.StatusCode);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretorA.Id || s.CorretorId == corretorB.Id).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            var ids = new[] { corretorA.Id, corretorB.Id };
            await limpeza.Corretores.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Conversa_de_outra_conta_autenticada_retorna_404_e_preserva_slot()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var contaDona = CriarCorretor(agora);
        var outraConta = CriarCorretor(agora);
        var corretor = CriarCorretor(agora);
        var slot = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.AddRange(contaDona, outraConta, corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);
            var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = conversa.LeadId;
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            conversa.Lead.RegistrarContato("Lead Outra Conta", "11999998888", null, agora);
            conversa.VincularConta(contaDona.Id);

            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(conversa.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var controller = CriarController(db);

            var identity = new ClaimsIdentity(
                [
                    new Claim(CorretorAuthenticationDefaults.CorretorIdClaim, outraConta.Id.ToString("D")),
                    new Claim(ClaimTypes.NameIdentifier, outraConta.Id.ToString("D")),
                    new Claim(CorretorAuthenticationDefaults.PerfilClaim, PerfisDoPainel.Cliente)
                ],
                CorretorAuthenticationDefaults.AuthenticationScheme);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };

            var resultado = await controller.RegistrarAgendamento(
                conversaId, new AgendamentoRequest(slot.Id), default);

            var obj = Assert.IsAssignableFrom<ObjectResult>(resultado.Result);
            Assert.Equal(StatusCodes.Status404NotFound, obj.StatusCode);
            var problem = Assert.IsType<ProblemDetails>(obj.Value);
            Assert.Equal("conversa nao encontrada", problem.Title);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotVerif = await verificacao.Slots.SingleAsync(s => s.Id == slot.Id);
            Assert.Null(slotVerif.LeadId);

            var totalMensagens = await verificacao.Mensagens.CountAsync(m => m.ConversaId == conversaId);
            Assert.Equal(0, totalMensagens);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Mensagens.Where(m => m.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            var idsCorretores = new[] { contaDona.Id, outraConta.Id, corretor.Id };
            await limpeza.Corretores.Where(c => idsCorretores.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Falha_de_savechanges_faz_rollback_da_transacao_e_da_reserva()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);
        var slot = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaId, agora, default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
            c.Lead.RegistrarContato("Lead Rollback", "11999996666", null, agora);
            c.DefinirDesfecho("direcionar_especialista");
            preparacao.Encaminhamentos.Add(
                Encaminhamento.Novo(c.Id, leadId, corretor.Id, corretor.Especialidade, agora));

            await preparacao.SaveChangesAsync();
        }

        try
        {
            var interceptor = new FalhaSaveChangesInterceptor { Ativo = true };
            var options = new DbContextOptionsBuilder<SolarDbContext>()
                .UseNpgsql(PostgresTestDatabase.ObterConexaoParaAplicacao())
                .AddInterceptors(interceptor)
                .Options;

            await using (var dbComFalha = new SolarDbContext(options))
            {
                var controllerComFalha = CriarController(dbComFalha);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    controllerComFalha.RegistrarAgendamento(
                        conversaId, new AgendamentoRequest(slot.Id), default));
            }

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var slotVerif = await verificacao.Slots.SingleAsync(s => s.Id == slot.Id);
            Assert.Null(slotVerif.LeadId);

            var mensagens = await verificacao.Mensagens.Where(m => m.ConversaId == conversaId).ToListAsync();
            Assert.Empty(mensagens);

            var conversaVerif = await verificacao.Conversas.SingleAsync(c => c.Id == conversaId);
            Assert.Equal("direcionar_especialista", conversaVerif.Desfecho);
        }
        finally
        {
            await using var limpeza = await PostgresTestDatabase.CriarContextoAsync();
            await limpeza.Slots.Where(s => s.CorretorId == corretor.Id).ExecuteDeleteAsync();
            await limpeza.Encaminhamentos.Where(e => e.ConversaId == conversaId).ExecuteDeleteAsync();
            await limpeza.Conversas.Where(c => c.Id == conversaId).ExecuteDeleteAsync();
            await limpeza.Leads.Where(l => l.Id == leadId).ExecuteDeleteAsync();
            await limpeza.Corretores.Where(c => c.Id == corretor.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Observacao_obrigatoria_S45_preserva_marcos_e_evita_novo_followup()
    {
        var agora = DateTimeOffset.UtcNow;
        var conversaId = Guid.NewGuid();
        Guid leadId;
        var corretor = CriarCorretor(agora);
        var slot = Slot.Novo(corretor.Id, agora.AddDays(1), agora.AddDays(1).AddHours(1));
        long encaminhamentoId;

        DateTimeOffset? intencaoEm;
        DateTimeOffset? essenciaisEm;
        DateTimeOffset? encaminhadaEm;
        DateTimeOffset? corretorAtribuidoEm;
        DateTimeOffset? primeiroReengajamentoEm;
        int tentativasReengajamento;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            preparacao.Corretores.Add(corretor);
            preparacao.Slots.Add(slot);

            var repo = new ConversaRepositorio(preparacao);
            var c = await repo.ObterOuCriarAsync(conversaId, agora.AddHours(-3), default);
            leadId = c.LeadId;
            c.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora.AddHours(-3));
            c.Lead.RegistrarContato("Lead S45", "11999997777", null, agora.AddHours(-2));

            c.Lead.Fundir(
                Intencoes.Compra,
                new CamposExtraidos(Nome: "Lead S45", PrecoMax: 600000, Quartos: 3, Urgencia: Urgencias.Alta),
                agora.AddHours(-2));

            var turnoHandoff = new TurnoResponse(
                "Encaminhando...",
                Intencoes.Compra,
                new CamposExtraidos(),
                ProximasAcoes.DirecionarEspecialista,
                [],
                null,
                EssenciaisCompletos: true);
            c.RegistrarTurno("Quero comprar", turnoHandoff, agora.AddHours(-2));

            var turnoFollowUp = new TurnoResponse(
                "Oi, tudo bem?",
                Intencoes.Compra,
                new CamposExtraidos(),
                ProximasAcoes.ContinuarConversa,
                [],
                null);
            c.RegistrarFollowUp(turnoFollowUp, agora.AddHours(-1));
            c.RegistrarCorretorAtribuido(agora.AddHours(-1));

            var enc = Encaminhamento.Novo(c.Id, leadId, corretor.Id, corretor.Especialidade, agora.AddHours(-1));
            preparacao.Encaminhamentos.Add(enc);

            await preparacao.SaveChangesAsync();

            var cPersistido = await preparacao.Conversas.AsNoTracking().SingleAsync(x => x.Id == conversaId);
            encaminhamentoId = enc.Id;
            intencaoEm = cPersistido.IntencaoEm;
            essenciaisEm = cPersistido.EssenciaisEm;
            encaminhadaEm = cPersistido.EncaminhadaEm;
            corretorAtribuidoEm = cPersistido.CorretorAtribuidoEm;
            primeiroReengajamentoEm = cPersistido.PrimeiroReengajamentoEm;
            tentativasReengajamento = cPersistido.TentativasReengajamento;

            Assert.NotNull(intencaoEm);
            Assert.NotNull(essenciaisEm);
            Assert.NotNull(encaminhadaEm);
            Assert.NotNull(corretorAtribuidoEm);
            Assert.NotNull(primeiroReengajamentoEm);
            Assert.Equal(1, tentativasReengajamento);
            Assert.Equal("direcionar_especialista", c.Desfecho);
        }

        try
        {
            var agenteHandler = new AgenteContadorHandler();
            await using (var db = await PostgresTestDatabase.CriarContextoAsync())
            {
                var controller = CriarController(db, agenteHandler);

                var resultado = await controller.RegistrarAgendamento(
                    conversaId, new AgendamentoRequest(slot.Id), default);

                var ok = Assert.IsType<OkObjectResult>(resultado.Result);
                var agendamento = Assert.IsType<AgendamentoDaConversa>(ok.Value);
                Assert.Equal(EstadosDoAgendamento.Confirmado, agendamento.Estado);
            }

            Assert.Equal(0, agenteHandler.Chamadas);

            await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
            var cVerif = await verificacao.Conversas.Include(c => c.Lead).SingleAsync(c => c.Id == conversaId);

            Assert.Equal(intencaoEm, cVerif.IntencaoEm);
            Assert.Equal(essenciaisEm, cVerif.EssenciaisEm);
            Assert.Equal(encaminhadaEm, cVerif.EncaminhadaEm);
            Assert.Equal(corretorAtribuidoEm, cVerif.CorretorAtribuidoEm);
            Assert.Equal(primeiroReengajamentoEm, cVerif.PrimeiroReengajamentoEm);
            Assert.Equal(tentativasReengajamento, cVerif.TentativasReengajamento);

            Assert.Equal(ProximasAcoes.AgendarReuniao, cVerif.Desfecho);

            Assert.Equal("Lead S45", cVerif.Lead.Nome);
            Assert.Equal(Intencoes.Compra, cVerif.Lead.Intencao);
            Assert.Equal(600000, cVerif.Lead.PrecoMax);
            Assert.Equal(3, cVerif.Lead.Quartos);
            Assert.Equal(Urgencias.Alta, cVerif.Lead.Urgencia);

            var encaminhamentos = await verificacao.Encaminhamentos.Where(e => e.ConversaId == conversaId).ToListAsync();
            Assert.Single(encaminhamentos);
            Assert.Equal(encaminhamentoId, encaminhamentos[0].Id);

            var mensagens = await verificacao.Mensagens.Where(m => m.ConversaId == conversaId).ToListAsync();
            Assert.Equal(5, mensagens.Count);

            var repoVerif = new ConversaRepositorio(verificacao);
            var inativas = await repoVerif.ObterIdsInativasParaFollowUpAsync(
                DateTimeOffset.UtcNow.AddDays(10), 10, default);
            Assert.DoesNotContain(conversaId, inativas);
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
        AgenteContadorHandler? agenteHandler = null)
    {
        var agenda = new AgendaRepositorio(db);
        var conversasRepo = new ConversaRepositorio(db);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);
        var travas = new TravaDeConversas();

        var handler = agenteHandler ?? new AgenteContadorHandler();
        var http = new HttpClient(handler)
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
            nome: $"Corretor T3 {id:N}",
            email: $"corretor-t3-{id:N}@teste.com",
            emailNormalizado: $"CORRETOR-T3-{id:N}@TESTE.COM",
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

    private sealed class AgenteContadorHandler : HttpMessageHandler
    {
        public int Chamadas => _chamadas;
        private int _chamadas;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _chamadas);
            throw new InvalidOperationException("Agente nao deve ser chamado em T3a.");
        }
    }

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Solar.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class FalhaSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool Ativo { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Ativo)
            {
                throw new InvalidOperationException("Falha simulada no SaveChangesAsync.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
