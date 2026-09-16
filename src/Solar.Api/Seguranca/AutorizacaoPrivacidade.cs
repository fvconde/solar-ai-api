using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Solar.Api.Seguranca;

public static class AutorizacaoPrivacidade
{
    public const string HeaderChavePrivacidade = "X-Chave-Privacidade";
    public const string HeaderAdminKey = "X-Admin-Key";

    /// <summary>
    /// Valida se a requisicao possui credencial autorizada para operacoes
    /// administrativas ou de eliminacao de dados da LGPD.
    /// Retorna ActionResult de erro (401, 403 ou 503) ou null se autorizado.
    /// A operacao e estritamente recusada quando a chave nao estiver configurada no servidor.
    /// </summary>
    public static ActionResult? Validar(
        HttpRequest request,
        IConfiguration configuracao,
        ControllerBase controller)
    {
        var chaveEsperada = configuracao["Seguranca:ChavePrivacidade"]
            ?? configuracao["Seguranca:ChaveAdmin"];

        if (string.IsNullOrWhiteSpace(chaveEsperada))
        {
            return controller.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "chave de autorizacao nao configurada no servidor");
        }

        string? chaveInformada = null;

        if (request.Headers.TryGetValue(HeaderChavePrivacidade, out var valorPrivacidade) &&
            !string.IsNullOrWhiteSpace(valorPrivacidade))
        {
            chaveInformada = valorPrivacidade.ToString();
        }
        else if (request.Headers.TryGetValue(HeaderAdminKey, out var valorAdmin) &&
                 !string.IsNullOrWhiteSpace(valorAdmin))
        {
            chaveInformada = valorAdmin.ToString();
        }
        else if (request.Headers.TryGetValue("Authorization", out var valorAuth) &&
                 !string.IsNullOrWhiteSpace(valorAuth))
        {
            var authStr = valorAuth.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                chaveInformada = authStr["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(chaveInformada))
        {
            return controller.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "autorizacao necessaria para exclusao de dados");
        }

        if (!string.Equals(chaveInformada, chaveEsperada, StringComparison.Ordinal))
        {
            return controller.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "chave de autorizacao invalida");
        }

        return null;
    }
}
