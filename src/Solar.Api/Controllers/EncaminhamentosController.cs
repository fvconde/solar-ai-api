using Microsoft.AspNetCore.Mvc;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Encaminhamentos;

namespace Solar.Api.Controllers;

[ApiController]
[Route("encaminhamentos")]
[Produces("application/json")]
public class EncaminhamentosController(
    EncaminhamentoRepositorio encaminhamentos,
    ConversaRepositorio conversas,
    ResumoClient agente,
    TravaDeConversas travas,
    IHostEnvironment environment,
    ILogger<EncaminhamentosController> logger) : ControllerBase
{
    [HttpPost("{id:long}/resumo")]
    [ProducesResponseType<ResumoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ResumoResponse>> ResumirAsync(
        long id,
        [FromQuery] bool forcar = false,
        CancellationToken cancellationToken = default)
    {
        var referencia = await encaminhamentos.ObterAsync(id, cancellationToken);
        if (referencia is null)
        {
            return NotFound();
        }

        using var _ = await travas.TravarAsync(referencia.ConversaId, cancellationToken);
        var encaminhamento = await encaminhamentos.ObterParaEscritaAsync(id, cancellationToken);
        if (encaminhamento is null)
        {
            return NotFound();
        }

        if (!forcar && encaminhamento.Resumo is not null)
        {
            return Ok(encaminhamento.Resumo);
        }

        var conversa = await conversas.ObterAsync(encaminhamento.ConversaId, cancellationToken);
        if (conversa is null)
        {
            return NotFound();
        }

        var historico = await conversas.HistoricoRecenteAsync(
            encaminhamento.ConversaId,
            ContratoTurno.LimiteHistorico,
            cancellationToken);
        var imoveis = await conversas.ObterImoveisSugeridosDaConversaAsync(
            encaminhamento.ConversaId,
            cancellationToken);
        var requisicao = new ResumoRequest(
            conversa.Lead.ParaContrato(),
            historico,
            imoveis.Take(ContratoTurno.LimiteImoveis).ToList());

        try
        {
            var resumo = await agente.GerarAsync(requisicao, cancellationToken);
            await encaminhamentos.GravarResumoAsync(encaminhamento, resumo, cancellationToken);

            return Ok(resumo);
        }
        catch (AgenteIndisponivelException erro)
        {
            logger.LogError(
                erro,
                "Resumo do encaminhamento {EncaminhamentoId} falhou",
                id);
            return this.Traduzir(erro, environment);
        }
    }
}
