using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Seguranca;

namespace Solar.Api.Controllers;

[ApiController]
[Route("leads")]
[Produces("application/json")]
public class LeadsController(
    ConversaRepositorio conversas,
    TravaDeConversas travas,
    IConfiguration configuracao,
    ILogger<LeadsController> logger) : ControllerBase
{
    /// <summary>
    /// Elimina definitivamente os dados de um lead e todos os seus registros
    /// vinculados (conversas, mensagens, encaminhamentos) em atendimento a LGPD.
    /// Exige autorizacao administrativa/privacidade via header X-Chave-Privacidade,
    /// X-Admin-Key ou Bearer token.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("exclusao")]
    [ProducesResponseType<ExclusaoLeadResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExclusaoLeadResponse>> Excluir(
        Guid id,
        CancellationToken cancellationToken)
    {
        var erroAuth = AutorizacaoPrivacidade.Validar(Request, configuracao, this);
        if (erroAuth is not null)
        {
            return erroAuth;
        }

        // Coordena a trava de concorrencia abrangendo o lead e todas as suas conversas
        var conversaIds = await conversas.ObterIdsDeConversasDoLeadAsync(id, cancellationToken);
        var idsParaTravar = conversaIds.Append(id);

        using var _ = await travas.TravarMultiplasAsync(idsParaTravar, cancellationToken);

        var resultado = await conversas.ExcluirLeadAsync(id, cancellationToken);

        if (resultado is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "lead nao encontrado");
        }

        logger.LogInformation(
            "Lead {LeadId} e seus registros vinculados foram eliminados por solicitacao LGPD ({ConversasAfetadas} conversas afetadas)",
            id, resultado.ConversasAfetadas);

        return Ok(new ExclusaoLeadResponse(
            resultado.LeadId,
            resultado.ConversasAfetadas,
            resultado.MensagensExcluidas,
            resultado.RemovidoEm,
            Escopo: "lead_e_vinculos"));
    }
}
