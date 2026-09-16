using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public class FollowUpPostgresTeste
{
    private static IServiceScopeFactory CriarScopeFactory(string connectionString, AgenteClient agente)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SolarDbContext>(builder => builder.UseNpgsql(connectionString));
        services.AddScoped<ConversaRepositorio>();
        services.AddScoped(_ => agente);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task LimparDadosAsync(SolarDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM mensagens; DELETE FROM encaminhamentos; DELETE FROM conversas; DELETE FROM leads;");
    }

    [Fact]
    public async Task Postgres_Verificaveis_1_e_2_Conversa_inativa_recebe_follow_up_citando_perfil_e_aparece_na_trilha()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await LimparDadosAsync(db);
        var conexao = db.Database.GetConnectionString()!;

        var conversaId = Guid.NewGuid();
        var tresMinutosAtras = DateTimeOffset.UtcNow.AddMinutes(-3);

        var conversa = Conversa.Nova(conversaId, Canais.Web, tresMinutosAtras);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tresMinutosAtras);
        conversa.RegistrarTurno(
            "Procuro apartamento no Tatuapé até 500 mil",
            new TurnoResponse(
                "Tatuapé tem excelentes opções nessa faixa de 500 mil. Quantos quartos precisa?",
                Intencoes.Compra,
                new CamposExtraidos(Regiao: "Tatuape", PrecoMax: 500000),
                ProximasAcoes.ContinuarConversa,
                [],
                null),
            tresMinutosAtras);

        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        TurnoRequest? requisicaoCapturada = null;
        string? headerTrigger = null;

        var respostaAgente = "Olá! Vi que você buscava apartamento no Tatuapé até R$ 500 mil. Surgiram novas unidades que se encaixam no seu perfil, quer dar uma olhada?";

        var handler = new TesteHttpHandler(req =>
        {
            headerTrigger = req.Headers.Contains("X-Solar-Trigger")
                ? string.Join(",", req.Headers.GetValues("X-Solar-Trigger"))
                : null;
            requisicaoCapturada = req.Content?.ReadFromJsonAsync<TurnoRequest>().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TurnoResponse(
                    respostaAgente,
                    Intencoes.Compra,
                    new CamposExtraidos(),
                    ProximasAcoes.ContinuarConversa,
                    [],
                    null))
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(conexao, agente);
        var travas = new TravaDeConversas();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:IntervaloInatividade"] = "00:02:00",
                ["FollowUp:LimiteTentativas"] = "2"
            })
            .Build();

        var servico = new ServicoDeReengajamento(scopeFactory, travas, config, NullLogger<ServicoDeReengajamento>.Instance);

        var processadas = await servico.ExecutarCicloAsync();

        Assert.Equal(1, processadas);
        Assert.NotNull(requisicaoCapturada);
        Assert.Equal("follow-up", headerTrigger);
        Assert.Equal("[reengajar]", requisicaoCapturada.Mensagem);
        Assert.Equal("Tatuape", requisicaoCapturada.PerfilLead.Regiao);
        Assert.Equal(500000, requisicaoCapturada.PerfilLead.PrecoMax);

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;
        await using var dbVerificacao = new SolarDbContext(options);
        var conversaAtualizada = await dbVerificacao.Conversas
            .Include(c => c.Mensagens)
            .SingleAsync(c => c.Id == conversaId);

        Assert.Equal(1, conversaAtualizada.TentativasReengajamento);
        Assert.Equal(3, conversaAtualizada.Mensagens.Count);

        var ultimaMensagem = conversaAtualizada.Mensagens.OrderBy(m => m.Em).Last();
        Assert.Equal(Papeis.Agente, ultimaMensagem.Papel);
        Assert.Equal(respostaAgente, ultimaMensagem.Texto);
        Assert.Contains("Tatuapé", ultimaMensagem.Texto);
        Assert.Contains("500 mil", ultimaMensagem.Texto);

        Assert.DoesNotContain(conversaAtualizada.Mensagens, m => m.Texto == "[reengajar]");

        var repo = new ConversaRepositorio(dbVerificacao);
        var historicoNaTrilha = await repo.HistoricoRecenteAsync(conversaId, 20, default);
        Assert.Equal(3, historicoNaTrilha.Count);
        Assert.Equal(respostaAgente, historicoNaTrilha.Last().Texto);
    }

    [Fact]
    public async Task Postgres_Verificavel_3_Terceira_varredura_nao_gera_terceira_tentativa()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await LimparDadosAsync(db);
        var conexao = db.Database.GetConnectionString()!;

        var conversaId = Guid.NewGuid();
        var dezMinutosAtras = DateTimeOffset.UtcNow.AddMinutes(-10);

        var conversa = Conversa.Nova(conversaId, Canais.Web, dezMinutosAtras);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, dezMinutosAtras);
        conversa.RegistrarTurno(
            "procurando imóvel",
            new TurnoResponse("Olá! Como posso ajudar?", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
            dezMinutosAtras);

        conversa.RegistrarFollowUp(
            new TurnoResponse("Follow-up 1", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
            dezMinutosAtras.AddMinutes(2));
        conversa.RegistrarFollowUp(
            new TurnoResponse("Follow-up 2", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
            dezMinutosAtras.AddMinutes(4));

        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(conexao, agente);
        var travas = new TravaDeConversas();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:IntervaloInatividade"] = "00:02:00",
                ["FollowUp:LimiteTentativas"] = "2"
            })
            .Build();

        var servico = new ServicoDeReengajamento(scopeFactory, travas, config, NullLogger<ServicoDeReengajamento>.Instance);

        var processadas = await servico.ExecutarCicloAsync();

        Assert.Equal(0, processadas);
        Assert.False(chamouAgente);

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;
        await using var dbVerificacao = new SolarDbContext(options);
        var conversaFinal = await dbVerificacao.Conversas
            .Include(c => c.Mensagens)
            .SingleAsync(c => c.Id == conversaId);

        Assert.Equal(2, conversaFinal.TentativasReengajamento);
        Assert.Equal(4, conversaFinal.Mensagens.Count);
    }

    [Fact]
    public async Task Postgres_Verificavel_4_Conversa_com_desfecho_ou_encaminhada_nao_recebe_follow_up()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await LimparDadosAsync(db);
        var conexao = db.Database.GetConnectionString()!;

        var dezMinutosAtras = DateTimeOffset.UtcNow.AddMinutes(-10);

        var conversaEncerradaId = Guid.NewGuid();
        var conversaEncerrada = Conversa.Nova(conversaEncerradaId, Canais.Web, dezMinutosAtras);
        conversaEncerrada.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, dezMinutosAtras);
        conversaEncerrada.RegistrarTurno(
            "não tenho mais interesse, obrigado",
            new TurnoResponse("Até logo! Se precisar estamos à disposição.", Intencoes.Indefinida, new CamposExtraidos(), ProximasAcoes.Encerrar, [], null),
            dezMinutosAtras);

        var conversaEncaminhadaId = Guid.NewGuid();
        var conversaEncaminhada = Conversa.Nova(conversaEncaminhadaId, Canais.Web, dezMinutosAtras);
        conversaEncaminhada.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, dezMinutosAtras);
        conversaEncaminhada.RegistrarTurno(
            "quero agendar com especialista",
            new TurnoResponse("Agendando com corretor.", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.AgendarReuniao, [], null),
            dezMinutosAtras);

        var encaminhamento = Encaminhamento.Novo(conversaEncaminhadaId, conversaEncaminhada.LeadId, null, Especialidades.Moradia, dezMinutosAtras);

        db.Conversas.AddRange(conversaEncerrada, conversaEncaminhada);
        db.Encaminhamentos.Add(encaminhamento);
        await db.SaveChangesAsync();

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(conexao, agente);
        var travas = new TravaDeConversas();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:IntervaloInatividade"] = "00:02:00",
                ["FollowUp:LimiteTentativas"] = "2"
            })
            .Build();

        var servico = new ServicoDeReengajamento(scopeFactory, travas, config, NullLogger<ServicoDeReengajamento>.Instance);

        var processadas = await servico.ExecutarCicloAsync();

        Assert.Equal(0, processadas);
        Assert.False(chamouAgente);
    }

    [Fact]
    public async Task Postgres_Verificavel_5_Falha_do_agente_mantem_atomicidade_sem_mensagem_parcial()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await LimparDadosAsync(db);
        var conexao = db.Database.GetConnectionString()!;

        var conversaId = Guid.NewGuid();
        var cincoMinutosAtras = DateTimeOffset.UtcNow.AddMinutes(-5);

        var conversa = Conversa.Nova(conversaId, Canais.Web, cincoMinutosAtras);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, cincoMinutosAtras);
        conversa.RegistrarTurno(
            "olá",
            new TurnoResponse("Olá! Como posso te ajudar hoje?", Intencoes.Indefinida, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
            cincoMinutosAtras);

        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        var handler = new TesteHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(conexao, agente);
        var travas = new TravaDeConversas();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FollowUp:IntervaloInatividade"] = "00:02:00",
                ["FollowUp:LimiteTentativas"] = "2"
            })
            .Build();

        var servico = new ServicoDeReengajamento(scopeFactory, travas, config, NullLogger<ServicoDeReengajamento>.Instance);

        var processadas = await servico.ExecutarCicloAsync();

        Assert.Equal(0, processadas);

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;
        await using var dbVerificacao = new SolarDbContext(options);
        var conversaAposFalha = await dbVerificacao.Conversas
            .Include(c => c.Mensagens)
            .SingleAsync(c => c.Id == conversaId);

        Assert.Equal(0, conversaAposFalha.TentativasReengajamento);
        Assert.Equal(2, conversaAposFalha.Mensagens.Count);
        Assert.Equal(cincoMinutosAtras.ToUnixTimeSeconds(), conversaAposFalha.AtualizadaEm.ToUnixTimeSeconds());
    }

    private sealed class TesteHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(callback(request));
        }
    }
}
