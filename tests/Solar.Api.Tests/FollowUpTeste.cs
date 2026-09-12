using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

public class FollowUpTeste
{
    private static SolarDbContext CriarBanco(string dbName)
    {
        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new SolarDbContext(options);
    }

    private static IServiceScopeFactory CriarScopeFactory(string dbName, AgenteClient agente)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SolarDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ConversaRepositorio>();
        services.AddScoped(_ => agente);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task Varredura_com_conversa_inativa_gera_follow_up_citando_perfil_e_sem_mensagem_do_lead()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;
        var tresMinutosAtras = agora.AddMinutes(-3);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, tresMinutosAtras);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tresMinutosAtras);
            conversa.RegistrarTurno(
                "quero comprar um apto no Tatuape ate 500 mil",
                new TurnoResponse(
                    "Tatuapé é uma ótima região. Qual sua urgência?",
                    Intencoes.Compra,
                    new CamposExtraidos { Regiao = "Tatuape", PrecoMax = 500000 },
                    ProximasAcoes.ContinuarConversa,
                    [],
                    null),
                tresMinutosAtras);

            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        TurnoRequest? requisicaoCapturada = null;
        string? headerTrigger = null;

        var handler = new TesteHttpHandler(req =>
        {
            headerTrigger = req.Headers.Contains("X-Solar-Trigger")
                ? string.Join(",", req.Headers.GetValues("X-Solar-Trigger"))
                : null;
            requisicaoCapturada = req.Content?.ReadFromJsonAsync<TurnoRequest>().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TurnoResponse(
                    "Lembrei da sua procura no Tatuapé até 500 mil. Surgiram novas oportunidades por lá, quer conferir?",
                    Intencoes.Compra,
                    new CamposExtraidos(),
                    ProximasAcoes.ContinuarConversa,
                    [],
                    null))
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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

        using (var db = CriarBanco(dbName))
        {
            var conversaAtualizada = await db.Conversas.Include(c => c.Mensagens).SingleAsync(c => c.Id == conversaId);
            Assert.Equal(1, conversaAtualizada.TentativasReengajamento);
            Assert.Equal(3, conversaAtualizada.Mensagens.Count);

            var ultimaMensagem = conversaAtualizada.Mensagens.Last();
            Assert.Equal(Papeis.Agente, ultimaMensagem.Papel);
            Assert.Contains("Tatuapé", ultimaMensagem.Texto);
            Assert.Contains("500 mil", ultimaMensagem.Texto);

            Assert.DoesNotContain(conversaAtualizada.Mensagens, m => m.Texto == "[reengajar]");
        }
    }

    [Fact]
    public async Task Terceira_varredura_nao_gera_terceira_tentativa_quando_limite_atingido()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var dezMinutosAtras = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, dezMinutosAtras);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, dezMinutosAtras);
            conversa.RegistrarTurno(
                "quero apto",
                new TurnoResponse("Ola", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                dezMinutosAtras);

            conversa.RegistrarFollowUp(
                new TurnoResponse("Follow-up 1", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                dezMinutosAtras.AddMinutes(2));
            conversa.RegistrarFollowUp(
                new TurnoResponse("Follow-up 2", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                dezMinutosAtras.AddMinutes(4));

            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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

        using (var db = CriarBanco(dbName))
        {
            var conversaFinal = await db.Conversas.Include(c => c.Mensagens).SingleAsync(c => c.Id == conversaId);
            Assert.Equal(2, conversaFinal.TentativasReengajamento);
            Assert.Equal(4, conversaFinal.Mensagens.Count);
        }
    }

    [Fact]
    public async Task Conversa_com_desfecho_encerrar_nao_recebe_follow_up()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var tempo = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, tempo);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tempo);
            conversa.RegistrarTurno(
                "tchau, nao quero mais",
                new TurnoResponse("Ate logo", Intencoes.Indefinida, new CamposExtraidos(), ProximasAcoes.Encerrar, [], null),
                tempo);

            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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
    public async Task Conversa_encaminhada_nao_recebe_follow_up()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var tempo = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, tempo);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tempo);
            conversa.RegistrarTurno(
                "quero falar com corretor",
                new TurnoResponse("Passando para corretor", Intencoes.Compra, new CamposExtraidos(), ProximasAcoes.AgendarReuniao, [], null),
                tempo);

            var encaminhamento = Encaminhamento.Novo(conversaId, conversa.LeadId, null, Especialidades.Moradia, tempo);

            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(encaminhamento);
            await db.SaveChangesAsync();
        }

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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
    public async Task Conversa_sem_consentimento_nao_recebe_follow_up()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var tempo = DateTimeOffset.UtcNow.AddMinutes(-10);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, tempo);
            // Sem consentimento
            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var chamouAgente = false;
        var handler = new TesteHttpHandler(_ =>
        {
            chamouAgente = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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
    public async Task Falha_do_agente_nao_deixa_mensagem_pela_metade_no_banco_nem_incrementa_contador()
    {
        var dbName = Guid.NewGuid().ToString();
        var conversaId = Guid.NewGuid();
        var tempo = DateTimeOffset.UtcNow.AddMinutes(-5);

        using (var db = CriarBanco(dbName))
        {
            var conversa = Conversa.Nova(conversaId, Canais.Web, tempo);
            conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, tempo);
            conversa.RegistrarTurno(
                "ola",
                new TurnoResponse("Ola, tudo bem?", Intencoes.Indefinida, new CamposExtraidos(), ProximasAcoes.ContinuarConversa, [], null),
                tempo);

            db.Conversas.Add(conversa);
            await db.SaveChangesAsync();
        }

        var handler = new TesteHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var scopeFactory = CriarScopeFactory(dbName, agente);
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

        using (var db = CriarBanco(dbName))
        {
            var conversaAposFalha = await db.Conversas.Include(c => c.Mensagens).SingleAsync(c => c.Id == conversaId);
            Assert.Equal(0, conversaAposFalha.TentativasReengajamento);
            Assert.Equal(2, conversaAposFalha.Mensagens.Count);
            Assert.Equal(tempo, conversaAposFalha.AtualizadaEm);
        }
    }

    [Fact]
    public async Task Guarda_do_sentinela_lead_enviando_reengajar_pelo_endpoint_publico_nao_aciona_follow_up()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CriarBanco(dbName);
        var conversaId = Guid.NewGuid();
        var agora = DateTimeOffset.UtcNow;

        var conversa = Conversa.Nova(conversaId, Canais.Web, agora);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, agora);
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        string? headerTriggerRecebido = null;
        var handler = new TesteHttpHandler(req =>
        {
            headerTriggerRecebido = req.Headers.Contains("X-Solar-Trigger")
                ? string.Join(",", req.Headers.GetValues("X-Solar-Trigger"))
                : null;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new TurnoResponse(
                    "Resposta comum do modelo para o texto recebido.",
                    Intencoes.Indefinida,
                    new CamposExtraidos(),
                    ProximasAcoes.ContinuarConversa,
                    [],
                    null))
            };
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://agente.test") };
        var agente = new AgenteClient(http, NullLogger<AgenteClient>.Instance);
        var travas = new TravaDeConversas();
        var config = new ConfigurationBuilder().Build();

        var conversasRepo = new ConversaRepositorio(db);
        var agendaRepo = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agendaRepo);
        var encaminhamentosRepo = new EncaminhamentoRepositorio(db);

        var controller = new ConversasController(
            conversasRepo,
            encaminhamentosRepo,
            agendaRepo,
            gravacao,
            travas,
            agente,
            config,
            null!,
            NullLogger<ConversasController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var resultado = await controller.Enviar(conversaId, new NovaMensagemRequest("[reengajar]"), default);

        var ok = Assert.IsType<OkObjectResult>(resultado.Result);
        var resposta = Assert.IsType<MensagemResponse>(ok.Value);

        // Header de follow-up interno nunca deve ser enviado na chamada publica
        Assert.Null(headerTriggerRecebido);

        var conversaAposEnvio = await db.Conversas.Include(c => c.Mensagens).SingleAsync(c => c.Id == conversaId);

        // Tentativas de follow-up permanece 0
        Assert.Equal(0, conversaAposEnvio.TentativasReengajamento);

        // A mensagem do lead foi gravada como mensagem comum do usuario
        Assert.Equal(2, conversaAposEnvio.Mensagens.Count);
        Assert.Equal(Papeis.Lead, conversaAposEnvio.Mensagens[0].Papel);
        Assert.Equal("[reengajar]", conversaAposEnvio.Mensagens[0].Texto);
    }

    private sealed class TesteHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(callback(request));
        }
    }
}
