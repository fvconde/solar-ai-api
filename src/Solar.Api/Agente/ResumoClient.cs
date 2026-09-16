using System.Net.Http.Json;
using System.Text.Json;
using Solar.Api.Contracts;

namespace Solar.Api.Agente;

public sealed class ResumoClient(HttpClient http, ILogger<ResumoClient> logger)
{
    private const string RotaResumo = "/resumo";

    public async Task<ResumoResponse> GerarAsync(
        ResumoRequest requisicao,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage resposta;

        try
        {
            resposta = await http.PostAsJsonAsync(RotaResumo, requisicao, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgenteIndisponivelException(
                $"o agente nao respondeu em {http.Timeout.TotalSeconds:0}s",
                tempoEsgotado: true,
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AgenteIndisponivelException(
                $"falha ao alcancar o agente em {http.BaseAddress}: {ex.Message}",
                tempoEsgotado: false,
                ex);
        }

        using (resposta)
        {
            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Agente respondeu {StatusCode} em {Rota}",
                    (int)resposta.StatusCode,
                    RotaResumo);

                throw new AgenteIndisponivelException(
                    $"o agente respondeu {(int)resposta.StatusCode}",
                    tempoEsgotado: false);
            }

            try
            {
                return await resposta.Content.ReadFromJsonAsync<ResumoResponse>(cancellationToken)
                       ?? throw new AgenteIndisponivelException(
                           "o agente devolveu um corpo vazio",
                           tempoEsgotado: false);
            }
            catch (JsonException ex)
            {
                throw new AgenteIndisponivelException(
                    "a resposta do agente nao casa com o contrato do POST /resumo",
                    tempoEsgotado: false,
                    ex);
            }
        }
    }
}
