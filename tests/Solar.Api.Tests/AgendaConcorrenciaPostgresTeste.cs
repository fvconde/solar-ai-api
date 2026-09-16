using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
public sealed class AgendaConcorrenciaPostgresTeste
{
    [Fact]
    public async Task Dois_posts_disputando_o_mesmo_slot_confirmam_um_unico_lead()
    {
        var conversaIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        Guid[] leadIds;
        long slotId;
        long[] slotIds;

        await using (var preparacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            var corretor = await preparacao.Corretores
                .AsNoTracking()
                .FirstAsync(corretor => corretor.Ativo);
            var repositorio = new ConversaRepositorio(preparacao);
            var primeira = await repositorio.ObterOuCriarAsync(conversaIds[0], DateTimeOffset.UtcNow, default);
            var segunda = await repositorio.ObterOuCriarAsync(conversaIds[1], DateTimeOffset.UtcNow, default);
            primeira.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
            segunda.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
            leadIds = [primeira.LeadId, segunda.LeadId];

            preparacao.Encaminhamentos.AddRange(
                Encaminhamento.Novo(
                    primeira.Id, primeira.LeadId, corretor.Id, corretor.Especialidade, DateTimeOffset.UtcNow),
                Encaminhamento.Novo(
                    segunda.Id, segunda.LeadId, corretor.Id, corretor.Especialidade, DateTimeOffset.UtcNow));

            var inicio = DateTimeOffset.UtcNow.AddMinutes(5);
            var slots = new[]
            {
                Slot.Novo(corretor.Id, inicio, inicio.AddHours(1)),
                Slot.Novo(corretor.Id, inicio.AddMinutes(10), inicio.AddMinutes(70)),
                Slot.Novo(corretor.Id, inicio.AddMinutes(20), inicio.AddMinutes(80))
            };
            preparacao.Slots.AddRange(slots);
            await preparacao.SaveChangesAsync();
            slotId = slots[0].Id;
            slotIds = slots.Select(slot => slot.Id).ToArray();
        }

        using var http = new HttpClient(new AgenteBarreiraHandler(slotId))
        {
            BaseAddress = new Uri("http://agente.test")
        };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var travas = new TravaDeConversas();

        await using var dbPrimeiro = await PostgresTestDatabase.CriarContextoAsync();
        await using var dbSegundo = await PostgresTestDatabase.CriarContextoAsync();
        var primeiro = CriarController(dbPrimeiro, agente, travas);
        var segundo = CriarController(dbSegundo, agente, travas);

        var resultados = await Task.WhenAll(
            primeiro.Enviar(conversaIds[0], new NovaMensagemRequest("Quero esse horario"), default),
            segundo.Enviar(conversaIds[1], new NovaMensagemRequest("Pode reservar para mim"), default));

        var respostas = resultados.Select(ExtrairResposta).ToArray();
        var confirmada = Assert.Single(
            respostas,
            resposta => resposta.Agendamento?.Estado == EstadosDoAgendamento.Confirmado);
        var indisponivel = Assert.Single(
            respostas,
            resposta => resposta.Agendamento?.Estado == EstadosDoAgendamento.Indisponivel);

        Assert.Equal(slotId, confirmada.Agendamento!.Horario!.Id);
        Assert.NotEmpty(indisponivel.Agendamento!.Alternativas);
        Assert.DoesNotContain(indisponivel.Agendamento!.Alternativas, slot => slot.Id == slotId);

        await using var verificacao = await PostgresTestDatabase.CriarContextoAsync();
        var donos = await verificacao.Slots
            .AsNoTracking()
            .Where(slot => slot.Id == slotId && slot.LeadId != null)
            .Select(slot => slot.LeadId!.Value)
            .Distinct()
            .ToListAsync();

        Assert.Single(donos);
        Assert.Contains(donos[0], leadIds);

        await verificacao.Slots.Where(slot => slotIds.Contains(slot.Id)).ExecuteDeleteAsync();
        var conversas = new ConversaRepositorio(verificacao);
        foreach (var leadId in leadIds)
        {
            await conversas.ExcluirLeadAsync(leadId, default);
        }
    }

    private static ConversasController CriarController(
        SolarDbContext db,
        AgenteClient agente,
        TravaDeConversas travas)
    {
        var agenda = new AgendaRepositorio(db);
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Conversas:JanelaHistorico"] = "20"
            })
            .Build();

        return new ConversasController(
            new ConversaRepositorio(db),
            new EncaminhamentoRepositorio(db),
            agenda,
            new GravacaoDoTurno(db, agenda),
            travas,
            agente,
            configuracao,
            null!,
            NullLogger<ConversasController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static MensagemResponse ExtrairResposta(ActionResult<MensagemResponse> resultado)
    {
        var ok = Assert.IsType<OkObjectResult>(resultado.Result);
        return Assert.IsType<MensagemResponse>(ok.Value);
    }

    private sealed class AgenteBarreiraHandler(long slotId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _duasChamadas =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _chamadas;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _chamadas) == 2)
            {
                _duasChamadas.TrySetResult();
            }

            await _duasChamadas.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TurnoResponse(
                    "Vou tentar reservar esse horario.",
                    Intencoes.Compra,
                    new CamposExtraidos(),
                    ProximasAcoes.AgendarReuniao,
                    [],
                    slotId))
            };
        }
    }
}
