using Microsoft.AspNetCore.Mvc;
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
    [ProducesResponseType<TurnoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<TurnoResponse>> Turno(
        TurnoRequest requisicao,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await agente.TurnoAsync(requisicao, cancellationToken));
        }
        catch (AgenteIndisponivelException ex)
        {
            logger.LogError(ex, "Turno da conversa {ConversaId} falhou", requisicao.ConversaId);

            return Problem(
                statusCode: ex.TempoEsgotado
                    ? StatusCodes.Status504GatewayTimeout
                    : StatusCodes.Status502BadGateway,
                title: "agente indisponivel",
                detail: environment.IsDevelopment() ? ex.Message : null);
        }
    }
}
