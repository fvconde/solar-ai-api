using Microsoft.AspNetCore.Mvc;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;

namespace Solar.Api.Controllers;

[ApiController]
[Route("conversas")]
[Produces("application/json")]
public class ConversasController(
    ConversaRepositorio conversas,
    TravaDeConversas travas,
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
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var agora = DateTimeOffset.UtcNow;
        var conversa = await conversas.ObterOuCriarAsync(id, agora, cancellationToken);
        var historico = await conversas.HistoricoRecenteAsync(id, Janela, cancellationToken);

        var turno = new TurnoRequest(
            id,
            requisicao.Texto,
            historico,
            conversa.Lead.ParaContrato());

        TurnoResponse resposta;

        try
        {
            // Fora de qualquer transacao de proposito: a chamada ao agente leva
            // segundos e pode levar ate 45, e segurar conexao do pool durante
            // isso esgotaria o banco muito antes de esgotar o Gemini.
            resposta = await agente.TurnoAsync(turno, cancellationToken);
        }
        catch (AgenteIndisponivelException erro)
        {
            logger.LogError(erro, "Turno da conversa {ConversaId} falhou", id);

            return this.Traduzir(erro, environment);
        }

        await conversas.RegistrarTurnoAsync(
            conversa, requisicao.Texto, resposta, DateTimeOffset.UtcNow, cancellationToken);

        return Ok(new MensagemResponse(
            id,
            resposta.Resposta,
            resposta.Intencao,
            resposta.ProximaAcao,
            conversa.Lead.ParaContrato(),
            resposta.ImoveisSugeridos));
    }

    /// <summary>Devolve o historico completo e o perfil acumulado da conversa.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ConversaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversaResponse>> Obter(Guid id, CancellationToken cancellationToken)
    {
        var conversa = await conversas.ObterAsync(id, cancellationToken);

        if (conversa is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        var mensagens = await conversas.HistoricoCompletoAsync(id, cancellationToken);

        return Ok(new ConversaResponse(id, conversa.Lead.ParaContrato(), mensagens));
    }
}
