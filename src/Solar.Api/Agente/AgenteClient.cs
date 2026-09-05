using System.Net.Http.Json;
using System.Text.Json;
using Solar.Api.Contracts;

namespace Solar.Api.Agente;

public sealed class AgenteIndisponivelException(string mensagem, bool tempoEsgotado, Exception? interna = null)
    : Exception(mensagem, interna)
{
    public bool TempoEsgotado { get; } = tempoEsgotado;
}

public sealed class AgenteClient(HttpClient http, ILogger<AgenteClient> logger)
{
    private const string RotaTurno = "/turn";

    public async Task<TurnoResponse> TurnoAsync(TurnoRequest requisicao, CancellationToken cancellationToken)
    {
        HttpResponseMessage resposta;

        try
        {
            resposta = await http.PostAsJsonAsync(RotaTurno, requisicao, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgenteIndisponivelException(
                $"o agente nao respondeu em {http.Timeout.TotalSeconds:0}s", tempoEsgotado: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AgenteIndisponivelException(
                $"falha ao alcancar o agente em {http.BaseAddress}: {ex.Message}", tempoEsgotado: false, ex);
        }

        using (resposta)
        {
            if (!resposta.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Agente respondeu {StatusCode} em {Rota}", (int)resposta.StatusCode, RotaTurno);

                throw new AgenteIndisponivelException(
                    $"o agente respondeu {(int)resposta.StatusCode}", tempoEsgotado: false);
            }

            try
            {
                return await resposta.Content.ReadFromJsonAsync<TurnoResponse>(cancellationToken)
                       ?? throw new AgenteIndisponivelException("o agente devolveu um corpo vazio", tempoEsgotado: false);
            }
            catch (JsonException ex)
            {
                throw new AgenteIndisponivelException(
                    "a resposta do agente nao casa com o contrato do POST /turn", tempoEsgotado: false, ex);
            }
        }
    }
}
