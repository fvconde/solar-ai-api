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

    public Task<TurnoResponse> TurnoAsync(TurnoRequest requisicao, CancellationToken cancellationToken) =>
        EnviarAsync(requisicao, followUp: false, cancellationToken);

    public Task<TurnoResponse> ReengajarAsync(TurnoRequest requisicao, CancellationToken cancellationToken) =>
        EnviarAsync(requisicao, followUp: true, cancellationToken);

    private async Task<TurnoResponse> EnviarAsync(TurnoRequest requisicao, bool followUp, CancellationToken cancellationToken)
    {
        HttpResponseMessage resposta;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RotaTurno);
            request.Content = JsonContent.Create(requisicao);
            if (followUp)
            {
                request.Headers.Add("X-Solar-Trigger", "follow-up");
            }
            resposta = await http.SendAsync(request, cancellationToken);
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
