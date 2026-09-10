using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solar.Api.Agente;
using Solar.Api.Contracts;

namespace Solar.Api.Controllers;

[ApiController]
[Produces("application/json")]
public class TurnoController(
    AgenteClient agente,
    IHostEnvironment environment,
    ILogger<TurnoController> logger) : ControllerBase
{
    /// <summary>Repassa um turno de conversa ao agente e devolve a resposta dele.</summary>
    [HttpPost("/turn")]
    [EnableRateLimiting("mensagens")]
    [ProducesResponseType<TurnoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<TurnoResponse>> Turno(
        TurnoRequest requisicao,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resposta = await agente.TurnoAsync(requisicao, cancellationToken);
            stopwatch.Stop();

            // Log estruturado sem dados pessoais (PII)
            logger.LogInformation(
                "Turno concluido para conversa {ConversaId} em {LatenciaMs}ms. Intencao={Intencao}, ProximaAcao={ProximaAcao}, QtdImoveis={QtdImoveis}",
                requisicao.ConversaId,
                stopwatch.ElapsedMilliseconds,
                resposta.Intencao,
                resposta.ProximaAcao,
                resposta.ImoveisSugeridos.Count);

            return Ok(resposta);
        }
        catch (AgenteIndisponivelException ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "Turno da conversa {ConversaId} falhou apos {LatenciaMs}ms", requisicao.ConversaId, stopwatch.ElapsedMilliseconds);

            return this.Traduzir(ex, environment);
        }
    }
}
