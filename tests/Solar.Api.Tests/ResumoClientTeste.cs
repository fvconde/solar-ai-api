using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Agente;
using Solar.Api.Contracts;

namespace Solar.Api.Tests;

public class ResumoClientTeste
{
    [Fact]
    public async Task Envia_o_contrato_tipado_para_post_resumo()
    {
        ResumoRequest? recebida = null;
        var handler = new TesteHttpHandler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/resumo", request.RequestUri?.AbsolutePath);
            recebida = await request.Content!.ReadFromJsonAsync<ResumoRequest>();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ResumoResponse(
                    "Compra para moradia.",
                    null,
                    null,
                    "Nao quer terreo.",
                    "Confirmar orcamento."))
            };
        });
        using var http = CriarHttp(handler);
        var cliente = new ResumoClient(http, NullLogger<ResumoClient>.Instance);

        var resposta = await cliente.GerarAsync(Requisicao(), CancellationToken.None);

        Assert.NotNull(recebida);
        Assert.Single(recebida.Historico);
        Assert.Equal("Nao quer terreo.", resposta.Objecoes);
        Assert.Null(resposta.Imoveis);
    }

    [Fact]
    public async Task Agente_parado_vira_502_sem_motivo_fora_de_development()
    {
        var handler = new TesteHttpHandler((_, _) =>
            throw new HttpRequestException("conexao recusada"));
        using var http = CriarHttp(handler);
        var cliente = new ResumoClient(http, NullLogger<ResumoClient>.Instance);

        var erro = await Assert.ThrowsAsync<AgenteIndisponivelException>(
            () => cliente.GerarAsync(Requisicao(), CancellationToken.None));
        var problema = Traduzir(erro, Environments.Production);

        Assert.False(erro.TempoEsgotado);
        Assert.Equal(StatusCodes.Status502BadGateway, problema.StatusCode);
        Assert.Null(Assert.IsType<ProblemDetails>(problema.Value).Detail);
    }

    [Fact]
    public async Task Tempo_esgotado_vira_504_com_motivo_so_em_development()
    {
        var handler = new TesteHttpHandler((_, _) =>
            throw new TaskCanceledException("tempo esgotado"));
        using var http = CriarHttp(handler);
        var cliente = new ResumoClient(http, NullLogger<ResumoClient>.Instance);

        var erro = await Assert.ThrowsAsync<AgenteIndisponivelException>(
            () => cliente.GerarAsync(Requisicao(), CancellationToken.None));
        var desenvolvimento = Traduzir(erro, Environments.Development);
        var producao = Traduzir(erro, Environments.Production);

        Assert.True(erro.TempoEsgotado);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, desenvolvimento.StatusCode);
        Assert.NotNull(Assert.IsType<ProblemDetails>(desenvolvimento.Value).Detail);
        Assert.Null(Assert.IsType<ProblemDetails>(producao.Value).Detail);
    }

    private static ResumoRequest Requisicao() =>
        new(
            new PerfilLead(Intencao: Intencoes.Compra, Regiao: "Pinheiros"),
            [new MensagemHistorico(Papeis.Lead, "Nao quero terreo.", DateTimeOffset.UtcNow)],
            []);

    private static HttpClient CriarHttp(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://agente.test"),
            Timeout = TimeSpan.FromSeconds(1)
        };

    private static ObjectResult Traduzir(AgenteIndisponivelException erro, string ambiente)
    {
        var controller = new ControllerTeste();
        return controller.Traduzir(erro, new AmbienteTeste(ambiente));
    }

    private sealed class ControllerTeste : ControllerBase;

    private sealed class AmbienteTeste(string ambiente) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = ambiente;
        public string ApplicationName { get; set; } = "Solar.Api.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TesteHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
}
