using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Solar.Api.Agente;

public sealed class HandlerAutenticacaoAgente(
    IOptions<OpcoesAutenticacaoAgente> opcoes,
    IProvedorTokenIdentidadeAgente provedor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage requisicao,
        CancellationToken cancellationToken)
    {
        if (!opcoes.Value.Ativa)
        {
            return await base.SendAsync(requisicao, cancellationToken);
        }

        var destino = requisicao.RequestUri;
        if (destino is null
            || !destino.IsAbsoluteUri
            || destino.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("A autenticacao do agente exige uma URL HTTPS absoluta.");
        }

        var audiencia = destino.GetLeftPart(UriPartial.Authority);
        var token = await provedor.ObterTokenAsync(audiencia, cancellationToken);
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(requisicao, cancellationToken);
    }
}
