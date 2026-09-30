using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solar.Api.Agente;
using Solar.Api.Contracts;

namespace Solar.Api.Tests;

public sealed class AutenticacaoAgenteHttpTeste
{
    private const string UrlAgente = "https://solar-agente.test/";
    private const string TokenDeTeste = "token-falso-1234567890";

    [Fact]
    public void Autenticacao_do_agente_fica_desligada_por_padrao()
    {
        Assert.False(new OpcoesAutenticacaoAgente().Ativa);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgenteClient_aplica_a_opcao_de_autenticacao(bool ativa)
    {
        var resultado = await ExecutarAsync(usarResumo: false, ativa);

        Assert.Null(resultado.Erro);
        Assert.NotNull(resultado.RequisicaoAgente);
        Assert.Equal("/turn", resultado.RequisicaoAgente.Caminho);
        AssertVerificarAutenticacao(resultado, ativa);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumoClient_aplica_a_opcao_de_autenticacao(bool ativa)
    {
        var resultado = await ExecutarAsync(usarResumo: true, ativa);

        Assert.Null(resultado.Erro);
        Assert.NotNull(resultado.RequisicaoAgente);
        Assert.Equal("/resumo", resultado.RequisicaoAgente.Caminho);
        AssertVerificarAutenticacao(resultado, ativa);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Falha_do_metadata_server_nao_vaza_token_em_erro(bool usarResumo)
    {
        const string sentinela = "token-secreto-que-nao-pode-vazar";
        var resultado = await ExecutarAsync(
            usarResumo,
            ativa: true,
            statusMetadata: HttpStatusCode.ServiceUnavailable,
            corpoMetadata: sentinela);

        Assert.NotNull(resultado.Erro);
        Assert.DoesNotContain(sentinela, resultado.Erro.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinela, resultado.Erro.Message, StringComparison.Ordinal);
        Assert.Null(resultado.RequisicaoAgente);
        Assert.NotNull(resultado.RequisicaoMetadata);
    }

    private static async Task<ResultadoChamada> ExecutarAsync(
        bool usarResumo,
        bool ativa,
        HttpStatusCode statusMetadata = HttpStatusCode.OK,
        string corpoMetadata = TokenDeTeste)
    {
        RequisicaoCapturada? requisicaoMetadata = null;
        RequisicaoCapturada? requisicaoAgente = null;

        using var httpMetadata = new HttpClient(new HandlerFalso((requisicao, _) =>
        {
            requisicaoMetadata = Capturar(requisicao, metadata: true);
            return Task.FromResult(new HttpResponseMessage(statusMetadata)
            {
                Content = new StringContent(corpoMetadata),
            });
        }));

        var autenticacao = new HandlerAutenticacaoAgente(
            Options.Create(new OpcoesAutenticacaoAgente { Ativa = ativa }),
            new ProvedorTokenIdentidadeMetadata(httpMetadata))
        {
            InnerHandler = new HandlerFalso((requisicao, _) =>
            {
                requisicaoAgente = Capturar(requisicao, metadata: false);
                var resposta = usarResumo
                    ? JsonContent.Create(new ResumoResponse("perfil", "orcamento", null, null, "proximo"))
                    : JsonContent.Create(new TurnoResponse(
                        "resposta",
                        Intencoes.Compra,
                        new CamposExtraidos(),
                        ProximasAcoes.ContinuarConversa,
                        [],
                        null));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = resposta,
                });
            }),
        };

        using var httpAgente = new HttpClient(autenticacao)
        {
            BaseAddress = new Uri(UrlAgente),
            Timeout = TimeSpan.FromSeconds(2),
        };

        AgenteIndisponivelException? erro = null;
        try
        {
            if (usarResumo)
            {
                var cliente = new ResumoClient(httpAgente, NullLogger<ResumoClient>.Instance);
                await cliente.GerarAsync(RequisicaoResumo(), CancellationToken.None);
            }
            else
            {
                var cliente = new AgenteClient(httpAgente, NullLogger<AgenteClient>.Instance);
                await cliente.TurnoAsync(RequisicaoTurno(), CancellationToken.None);
            }
        }
        catch (AgenteIndisponivelException ex)
        {
            erro = ex;
        }

        return new ResultadoChamada(requisicaoMetadata, requisicaoAgente, erro);
    }

    private static void AssertVerificarAutenticacao(ResultadoChamada resultado, bool ativa)
    {
        if (!ativa)
        {
            Assert.Null(resultado.RequisicaoMetadata);
            Assert.Null(resultado.RequisicaoAgente!.EsquemaAutenticacao);
            Assert.Null(resultado.RequisicaoAgente.ParametroAutenticacao);
            return;
        }

        Assert.NotNull(resultado.RequisicaoMetadata);
        Assert.Equal("metadata.google.internal", resultado.RequisicaoMetadata.Host);
        Assert.Equal(
            "/computeMetadata/v1/instance/service-accounts/default/identity",
            resultado.RequisicaoMetadata.Caminho);
        Assert.Equal("Google", resultado.RequisicaoMetadata.MetadataFlavor);
        Assert.Equal(UrlAgente, resultado.RequisicaoMetadata.Audiencia);
        Assert.Equal("Bearer", resultado.RequisicaoAgente!.EsquemaAutenticacao);
        Assert.Equal(TokenDeTeste, resultado.RequisicaoAgente.ParametroAutenticacao);
    }

    private static RequisicaoCapturada Capturar(HttpRequestMessage requisicao, bool metadata)
    {
        string? audiencia = null;
        if (metadata && requisicao.RequestUri is { } uri)
        {
            var parAudiencia = uri.Query
                .TrimStart('?')
                .Split('&')
                .Single(par => par.StartsWith("audience=", StringComparison.Ordinal));
            audiencia = Uri.UnescapeDataString(parAudiencia["audience=".Length..]);
        }

        var metadataFlavor = requisicao.Headers.TryGetValues("Metadata-Flavor", out var valoresMetadata)
            ? valoresMetadata.Single()
            : null;

        return new RequisicaoCapturada(
            requisicao.RequestUri?.Host,
            requisicao.RequestUri?.AbsolutePath,
            audiencia,
            requisicao.Headers.Authorization?.Scheme,
            requisicao.Headers.Authorization?.Parameter,
            metadataFlavor);
    }

    private static TurnoRequest RequisicaoTurno() =>
        new(Guid.NewGuid(), "Ola", [], new PerfilLead(Intencao: Intencoes.Compra), []);

    private static ResumoRequest RequisicaoResumo() =>
        new(
            new PerfilLead(Intencao: Intencoes.Compra),
            [new MensagemHistorico(Papeis.Lead, "Ola", DateTimeOffset.UtcNow)],
            []);

    private sealed record RequisicaoCapturada(
        string? Host,
        string? Caminho,
        string? Audiencia,
        string? EsquemaAutenticacao,
        string? ParametroAutenticacao,
        string? MetadataFlavor);

    private sealed record ResultadoChamada(
        RequisicaoCapturada? RequisicaoMetadata,
        RequisicaoCapturada? RequisicaoAgente,
        AgenteIndisponivelException? Erro);

    private sealed class HandlerFalso(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> enviar)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage requisicao,
            CancellationToken cancellationToken) =>
            enviar(requisicao, cancellationToken);
    }
}
