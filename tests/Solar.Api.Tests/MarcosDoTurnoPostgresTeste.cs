using System.Net;
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
public sealed class MarcosDoTurnoPostgresTeste
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 10, 5, 10, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 5, 10, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 10, 5, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Intencao_nula_ou_indefinida_nao_grava_intencao_em_e_primeira_valida_fica_imutavel()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);
        await db.SaveChangesAsync();

        try
        {
            var turnoIndefinido = new TurnoResponse(
                Resposta: "Ola! Como posso ajudar?",
                Intencao: Intencoes.Indefinida,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            repo.AplicarTurno(conversa, "Ola", turnoIndefinido, T1);
            await gravacao.SalvarAsync(conversa, null, [], null, T1, default);

            var recarregada1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Null(recarregada1.IntencaoEm);

            var turnoCompra = new TurnoResponse(
                Resposta: "Temos opcoes para compra.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: "Pinheiros"),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            repo.AplicarTurno(conversa, "Quero comprar em Pinheiros", turnoCompra, T2);
            await gravacao.SalvarAsync(conversa, null, [], null, T2, default);

            var recarregada2 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T2, recarregada2.IntencaoEm);

            var turnoAluguel = new TurnoResponse(
                Resposta: "Ou prefere aluguel?",
                Intencao: Intencoes.Aluguel,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            repo.AplicarTurno(conversa, "Na verdade quero alugar", turnoAluguel, T3);
            await gravacao.SalvarAsync(conversa, null, [], null, T3, default);

            var recarregada3 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T2, recarregada3.IntencaoEm);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Perfil_completo_antes_do_primeiro_turno_preserva_essenciais_nulo_e_true_grava_sem_apagar_posterior()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);

        conversa.Lead.Fundir(
            Intencoes.Compra,
            new CamposExtraidos(
                Nome: "Ana",
                Regiao: "Moema",
                PrecoMax: 900000,
                Quartos: 2,
                Urgencia: Urgencias.Alta,
                Score: 85),
            T0);
        await db.SaveChangesAsync();

        try
        {
            var antesDoTurno = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Null(antesDoTurno.EssenciaisEm);

            var turnoCompletos = new TurnoResponse(
                Resposta: "Perfil completo confirmado.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true);

            repo.AplicarTurno(conversa, "Confirmo os dados", turnoCompletos, T1);
            await gravacao.SalvarAsync(conversa, null, [], null, T1, default);

            var recarregada1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada1.EssenciaisEm);

            var turnoIncompletos = new TurnoResponse(
                Resposta: "Continuando a conversa.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            repo.AplicarTurno(conversa, "Tudo bem", turnoIncompletos, T2);
            await gravacao.SalvarAsync(conversa, null, [], null, T2, default);

            var recarregada2 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada2.EssenciaisEm);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Resposta_encaminhando_sem_essenciais_grava_apenas_encaminhada_em_e_essenciais_posteriores_gravam_sem_mudar_encaminhamento()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);
        await db.SaveChangesAsync();

        try
        {
            var turnoEncaminhaSemEssenciais = new TurnoResponse(
                Resposta: "Vou agendar com um especialista.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            repo.AplicarTurno(conversa, "Quero falar com um corretor agora", turnoEncaminhaSemEssenciais, T1);
            await gravacao.SalvarAsync(conversa, null, [], null, T1, default);

            var recarregada1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada1.EncaminhadaEm);
            Assert.Null(recarregada1.EssenciaisEm);

            var turnoFechaEssenciais = new TurnoResponse(
                Resposta: "Entendi suas preferencias.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: "Jardins", PrecoMax: 1500000),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true);

            repo.AplicarTurno(conversa, "Busco nos Jardins ate 1.5M", turnoFechaEssenciais, T2);
            await gravacao.SalvarAsync(conversa, null, [], null, T2, default);

            var recarregada2 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada2.EncaminhadaEm);
            Assert.Equal(T2, recarregada2.EssenciaisEm);

            var turnoNovoEncaminhamento = new TurnoResponse(
                Resposta: "Redirecionando ao especialista.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.DirecionarEspecialista,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true);

            repo.AplicarTurno(conversa, "Pode direcionar", turnoNovoEncaminhamento, T3);
            await gravacao.SalvarAsync(conversa, null, [], null, T3, default);

            var recarregada3 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada3.EncaminhadaEm);
            Assert.Equal(T2, recarregada3.EssenciaisEm);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Encaminhamento_existente_sem_marco_grava_encaminhada_em_e_preserva_linha_unica()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var corretor = await db.Corretores.AsNoTracking().FirstAsync(c => c.VinculoAtivo);
        var conversaId = Guid.NewGuid();
        var conversa = Conversa.Nova(conversaId, Canais.Web, T0);
        db.Conversas.Add(conversa);

        var encaminhamentoExistente = Encaminhamento.Novo(conversa.Id, conversa.LeadId, corretor.Id, corretor.Especialidades[0], T0);
        db.Encaminhamentos.Add(encaminhamentoExistente);
        await db.SaveChangesAsync();

        try
        {
            var antesDoTurno = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Null(antesDoTurno.EncaminhadaEm);

            var turno = new TurnoResponse(
                Resposta: "Agendando reuniao.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            conversa.RegistrarTurno("Quero agendar", turno, T1);
            await gravacao.SalvarAsync(conversa, null, [], null, T1, default);

            var recarregada = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
            Assert.Equal(T1, recarregada.EncaminhadaEm);

            var encaminhamentos = await db.Encaminhamentos.AsNoTracking().Where(e => e.ConversaId == conversaId).ToListAsync();
            var unico = Assert.Single(encaminhamentos);
            Assert.Equal(encaminhamentoExistente.Id, unico.Id);
            Assert.Equal(corretor.Id, unico.CorretorId);
            Assert.Equal(T0, unico.Em);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Transacao_de_gravacao_do_turno_que_falha_no_save_changes_nao_grava_marcos_nem_mensagens()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);
        await db.SaveChangesAsync();

        try
        {
            var turno = new TurnoResponse(
                Resposta: "Reuniao agendada.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(Regiao: "Pinheiros"),
                ProximaAcao: ProximasAcoes.AgendarReuniao,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: true);

            repo.AplicarTurno(conversa, "Agendar para compra", turno, T1);

            Assert.Equal(T1, conversa.IntencaoEm);
            Assert.Equal(T1, conversa.EssenciaisEm);
            Assert.Equal(T1, conversa.EncaminhadaEm);
            Assert.NotEmpty(conversa.Mensagens);

            var corretorInexistenteId = Guid.NewGuid();
            var encaminhamentoInvalido = Encaminhamento.Novo(
                conversa.Id,
                conversa.LeadId,
                corretorInexistenteId,
                Especialidades.Moradia,
                T1);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                gravacao.SalvarAsync(conversa, encaminhamentoInvalido, [], null, T1, default));

            db.ChangeTracker.Clear();

            var recarregada = await db.Conversas
                .Include(c => c.Mensagens)
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversaId);

            Assert.Null(recarregada.IntencaoEm);
            Assert.Null(recarregada.EssenciaisEm);
            Assert.Null(recarregada.EncaminhadaEm);
            Assert.Null(recarregada.CorretorAtribuidoEm);
            Assert.Null(recarregada.PrimeiroReengajamentoEm);
            Assert.Empty(recarregada.Mensagens);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Agente_falhando_com_503_nao_grava_marcos_nem_mensagens()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, T0);
        await db.SaveChangesAsync();

        try
        {
            using var http = new HttpClient(new Agente503Handler())
            {
                BaseAddress = new Uri("http://agente.test")
            };
            var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
            var travas = new TravaDeConversas();
            var controller = CriarController(db, agente, travas);

            var resultado = await controller.Enviar(conversaId, new NovaMensagemRequest("Quero apartamento"), default);
            var objeto = Assert.IsType<ObjectResult>(resultado.Result);
            Assert.Equal(StatusCodes.Status502BadGateway, objeto.StatusCode);

            db.ChangeTracker.Clear();
            var recarregada = await db.Conversas
                .Include(c => c.Mensagens)
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversaId);

            Assert.Null(recarregada.IntencaoEm);
            Assert.Null(recarregada.EssenciaisEm);
            Assert.Null(recarregada.EncaminhadaEm);
            Assert.Null(recarregada.CorretorAtribuidoEm);
            Assert.Null(recarregada.PrimeiroReengajamentoEm);
            Assert.Empty(recarregada.Mensagens);
        }
        finally
        {
            await LimparAsync(db, conversaId);
        }
    }

    [Fact]
    public async Task Dois_follow_ups_preservam_primeiro_reengajamento_em_e_incrementam_tentativas()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, T0, default);
        await db.SaveChangesAsync();

        try
        {
            var turno1 = new TurnoResponse(
                Resposta: "Ola, ainda tem interesse em imoveis?",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            await repo.GravarFollowUpAsync(conversa, turno1, T1, default);

            var recarregada1 = await db.Conversas
                .Include(c => c.Mensagens)
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversaId);

            Assert.Equal(T1, recarregada1.PrimeiroReengajamentoEm);
            Assert.Equal(1, recarregada1.TentativasReengajamento);
            Assert.Single(recarregada1.Mensagens);

            var turno2 = new TurnoResponse(
                Resposta: "Temos novas oportunidades na regiao.",
                Intencao: Intencoes.Compra,
                CamposExtraidos: new CamposExtraidos(),
                ProximaAcao: ProximasAcoes.ContinuarConversa,
                ImoveisSugeridos: [],
                SlotEscolhido: null,
                EssenciaisCompletos: false);

            await repo.GravarFollowUpAsync(conversa, turno2, T2, default);

            var recarregada2 = await db.Conversas
                .Include(c => c.Mensagens)
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversaId);

            Assert.Equal(T1, recarregada2.PrimeiroReengajamentoEm);
            Assert.Equal(2, recarregada2.TentativasReengajamento);
            Assert.Equal(2, recarregada2.Mensagens.Count);
        }
        finally
        {
            await LimparAsync(db, conversaId);
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
            new AmbienteTeste(),
            NullLogger<ConversasController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static async Task LimparAsync(SolarDbContext db, params Guid[] conversaIds)
    {
        if (conversaIds.Length == 0) return;

        var conversas = await db.Conversas
            .Include(c => c.Lead)
            .Where(c => conversaIds.Contains(c.Id))
            .ToListAsync();

        var leadIds = conversas.Select(c => c.LeadId).Distinct().ToList();

        var encaminhamentos = await db.Encaminhamentos
            .Where(e => conversaIds.Contains(e.ConversaId))
            .ToListAsync();

        db.Encaminhamentos.RemoveRange(encaminhamentos);
        db.Conversas.RemoveRange(conversas);

        var leads = await db.Leads.Where(l => leadIds.Contains(l.Id)).ToListAsync();
        db.Leads.RemoveRange(leads);

        await db.SaveChangesAsync();
    }

    private sealed class Agente503Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Solar.Api.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
