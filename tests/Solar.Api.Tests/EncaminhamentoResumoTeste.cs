using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public class EncaminhamentoResumoTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Primeira_leitura_gera_e_segunda_devolve_o_resumo_gravado()
    {
        using var db = CriarBanco();
        var encaminhamento = await CriarEncaminhamentoAsync(db);
        var chamadas = 0;
        ResumoRequest? recebida = null;
        var handler = new TesteHttpHandler(async request =>
        {
            chamadas++;
            recebida = await request.Content!.ReadFromJsonAsync<ResumoRequest>();
            return Resposta("Resumo gerado");
        });
        using var http = CriarHttp(handler);
        var controller = CriarController(db, http);

        var primeira = Extrair(await controller.ResumirAsync(encaminhamento.Id));
        var segunda = Extrair(await controller.ResumirAsync(encaminhamento.Id));

        Assert.Equal("Resumo gerado", primeira.Perfil);
        Assert.Equal(primeira, segunda);
        Assert.Equal(1, chamadas);
        Assert.NotNull(recebida);
        Assert.Equal(Intencoes.Compra, recebida.PerfilLead.Intencao);
        Assert.Equal(2, recebida.Historico.Count);
        Assert.Single(recebida.Imoveis);
        Assert.Equal("IMV-001", recebida.Imoveis[0].Id);
        Assert.Equal("Resumo gerado", encaminhamento.Resumo?.Perfil);
    }

    [Fact]
    public async Task Forcar_gera_de_novo_e_so_entao_substitui_o_valor_gravado()
    {
        using var db = CriarBanco();
        var encaminhamento = await CriarEncaminhamentoAsync(db);
        var chamadas = 0;
        var handler = new TesteHttpHandler(_ =>
        {
            chamadas++;
            return Task.FromResult(Resposta($"Resumo {chamadas}"));
        });
        using var http = CriarHttp(handler);
        var controller = CriarController(db, http);

        Extrair(await controller.ResumirAsync(encaminhamento.Id));
        var atualizado = Extrair(await controller.ResumirAsync(encaminhamento.Id, forcar: true));

        Assert.Equal(2, chamadas);
        Assert.Equal("Resumo 2", atualizado.Perfil);
        Assert.Equal("Resumo 2", encaminhamento.Resumo?.Perfil);
    }

    [Fact]
    public async Task Falha_do_agente_mantem_encaminhamento_com_resumo_nulo_e_permite_tentar_de_novo()
    {
        using var db = CriarBanco();
        var encaminhamento = await CriarEncaminhamentoAsync(db);
        var chamadas = 0;
        var handler = new TesteHttpHandler(_ =>
        {
            chamadas++;
            return Task.FromResult(chamadas == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Resposta("Recuperado"));
        });
        using var http = CriarHttp(handler);
        var controller = CriarController(db, http);

        var falha = await controller.ResumirAsync(encaminhamento.Id);

        Assert.Equal(
            StatusCodes.Status502BadGateway,
            Assert.IsType<ObjectResult>(falha.Result).StatusCode);
        Assert.Null(encaminhamento.Resumo);
        Assert.Equal(1, await db.Encaminhamentos.CountAsync());

        var recuperado = Extrair(await controller.ResumirAsync(encaminhamento.Id));
        Assert.Equal("Recuperado", recuperado.Perfil);
        Assert.Equal(2, chamadas);
    }

    [Fact]
    public async Task Falha_ao_forcar_preserva_o_resumo_anterior()
    {
        using var db = CriarBanco();
        var encaminhamento = await CriarEncaminhamentoAsync(db);
        var chamadas = 0;
        var handler = new TesteHttpHandler(_ =>
        {
            chamadas++;
            return Task.FromResult(chamadas == 1
                ? Resposta("Resumo preservado")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var http = CriarHttp(handler);
        var controller = CriarController(db, http);

        Extrair(await controller.ResumirAsync(encaminhamento.Id));
        var falha = await controller.ResumirAsync(encaminhamento.Id, forcar: true);

        Assert.Equal(
            StatusCodes.Status502BadGateway,
            Assert.IsType<ObjectResult>(falha.Result).StatusCode);
        Assert.Equal("Resumo preservado", encaminhamento.Resumo?.Perfil);
        Assert.Equal(2, chamadas);
    }

    [Fact]
    public async Task Encaminhamento_inexistente_devolve_404_sem_chamar_o_agente()
    {
        using var db = CriarBanco();
        var chamadas = 0;
        var handler = new TesteHttpHandler(_ =>
        {
            chamadas++;
            return Task.FromResult(Resposta("Nao deveria gerar"));
        });
        using var http = CriarHttp(handler);
        var controller = CriarController(db, http);

        var resultado = await controller.ResumirAsync(999);

        Assert.IsType<NotFoundResult>(resultado.Result);
        Assert.Equal(0, chamadas);
    }

    private static SolarDbContext CriarBanco()
    {
        var opcoes = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SolarDbContext(opcoes);
    }

    private static async Task<Encaminhamento> CriarEncaminhamentoAsync(SolarDbContext db)
    {
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora);
        conversa.RegistrarTurno(
            "Quero Pinheiros, mas nao quero terreo.",
            new TurnoResponse(
                "Encontrei uma opcao.",
                Intencoes.Compra,
                new CamposExtraidos(Regiao: "Pinheiros", PrecoMax: 900000, Score: 80),
                ProximasAcoes.DirecionarEspecialista,
                [new ImovelSugerido(
                    "IMV-001",
                    "apartamento",
                    "Pinheiros",
                    2,
                    72,
                    850000,
                    null,
                    "Dentro da faixa e da regiao")],
                null),
            Agora);
        var encaminhamento = Encaminhamento.Novo(
            conversa.Id,
            conversa.LeadId,
            null,
            Especialidades.Moradia,
            Agora);

        db.Conversas.Add(conversa);
        db.Encaminhamentos.Add(encaminhamento);
        await db.SaveChangesAsync();

        return encaminhamento;
    }

    private static EncaminhamentosController CriarController(
        SolarDbContext db,
        HttpClient http) =>
        new(
            new EncaminhamentoRepositorio(db),
            new ConversaRepositorio(db),
            new ResumoClient(http, NullLogger<ResumoClient>.Instance),
            new TravaDeConversas(),
            new AmbienteTeste(),
            NullLogger<EncaminhamentosController>.Instance);

    private static HttpClient CriarHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://agente.test") };

    private static HttpResponseMessage Resposta(string perfil) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ResumoResponse(
                perfil,
                null,
                null,
                "Nao quer terreo.",
                "Entrar em contato."))
        };

    private static ResumoResponse Extrair(ActionResult<ResumoResponse> resultado) =>
        Assert.IsType<ResumoResponse>(Assert.IsType<OkObjectResult>(resultado.Result).Value);

    private sealed class AmbienteTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Solar.Api.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TesteHttpHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request);
    }
}
