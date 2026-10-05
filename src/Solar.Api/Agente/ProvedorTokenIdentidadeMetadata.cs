namespace Solar.Api.Agente;

public interface IProvedorTokenIdentidadeAgente
{
    Task<string> ObterTokenAsync(string audiencia, CancellationToken cancellationToken);
}

public sealed class ProvedorTokenIdentidadeMetadata(HttpClient http) : IProvedorTokenIdentidadeAgente
{
    private static readonly Uri EndpointIdentidade = new(
        "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity",
        UriKind.Absolute);

    private const string ErroMetadata = "Nao foi possivel obter token de identidade do metadata server.";

    public async Task<string> ObterTokenAsync(string audiencia, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(audiencia, UriKind.Absolute, out var uriAudiencia)
            || uriAudiencia.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("A audiencia do token de identidade deve ser uma URL HTTPS absoluta.");
        }

        try
        {
            var uriMetadata = new UriBuilder(EndpointIdentidade)
            {
                Query = $"audience={Uri.EscapeDataString(audiencia)}",
            }.Uri;
            using var requisicao = new HttpRequestMessage(HttpMethod.Get, uriMetadata);
            requisicao.Headers.TryAddWithoutValidation("Metadata-Flavor", "Google");

            using var resposta = await http.SendAsync(requisicao, cancellationToken);
            if (!resposta.IsSuccessStatusCode)
            {
                throw new HttpRequestException(ErroMetadata);
            }

            var token = (await resposta.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (!TokenValido(token))
            {
                throw new HttpRequestException(ErroMetadata);
            }

            return token;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Nao repasse corpo, URL, token ou excecao do transporte para logs e respostas da API.
            throw new HttpRequestException(ErroMetadata);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(ErroMetadata);
        }
    }

    private static bool TokenValido(string token) =>
        token.Length is > 0 and <= 16_384
        && token.All(caractere =>
            char.IsAsciiLetterOrDigit(caractere)
            || caractere is '.' or '-' or '_');
}
