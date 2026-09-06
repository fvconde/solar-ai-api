using Microsoft.AspNetCore.Mvc;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;

namespace Solar.Api.Controllers;

[ApiController]
[Route("conversas")]
[Produces("application/json")]
public class ConversasController(
    ConversaStore conversas,
    AgenteClient agente,
    IConfiguration configuracao,
    IHostEnvironment environment,
    ILogger<ConversasController> logger) : ControllerBase
{
    private const int JanelaPadrao = 20;

    private int Janela => Math.Clamp(
        configuracao.GetValue("Conversas:JanelaHistorico", JanelaPadrao),
        2,
        ContratoTurno.LimiteHistorico);

    /// <summary>Envia uma mensagem do lead e devolve a resposta da Lia.</summary>
    [HttpPost("{id:guid}/mensagens")]
    [ProducesResponseType<MensagemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<MensagemResponse>> Enviar(
        Guid id,
        NovaMensagemRequest requisicao,
        CancellationToken cancellationToken)
    {
        var conversa = conversas.ObterOuCriar(id);

        using var _ = await conversas.TravarAsync(id, cancellationToken);

        var turno = new TurnoRequest(
            id,
            requisicao.Texto,
            conversa.HistoricoRecente(Janela),
            conversa.Perfil);

        TurnoResponse resposta;

        try
        {
            resposta = await agente.TurnoAsync(turno, cancellationToken);
        }
        catch (AgenteIndisponivelException erro)
        {
            logger.LogError(erro, "Turno da conversa {ConversaId} falhou", id);

            return this.Traduzir(erro, environment);
        }

        conversa.RegistrarTurno(requisicao.Texto, resposta, DateTimeOffset.UtcNow);

        return Ok(new MensagemResponse(
            id,
            resposta.Resposta,
            resposta.Intencao,
            resposta.ProximaAcao,
            conversa.Perfil,
            resposta.ImoveisSugeridos));
    }

    /// <summary>Devolve o historico completo e o perfil acumulado da conversa.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ConversaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public ActionResult<ConversaResponse> Obter(Guid id)
    {
        var conversa = conversas.Obter(id);

        if (conversa is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        return Ok(new ConversaResponse(id, conversa.Perfil, conversa.Mensagens));
    }
}
