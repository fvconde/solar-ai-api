using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Solar.Api.Agendamentos;
using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Seguranca;

namespace Solar.Api.Controllers;

[ApiController]
[Route("conversas")]
[Produces("application/json")]
public class ConversasController(
    ConversaRepositorio conversas,
    EncaminhamentoRepositorio encaminhamentos,
    AgendaRepositorio agenda,
    GravacaoDoTurno gravacao,
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

    [HttpPost("{id:guid}/consentimento")]
    [EnableRateLimiting("mensagens")]
    [ProducesResponseType<ConsentimentoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsentimentoResponse>> RegistrarConsentimento(
        Guid id,
        ConsentimentoRequest requisicao,
        CancellationToken cancellationToken)
    {
        if (requisicao.VersaoAvisoPrivacidade != AvisoPrivacidade.VersaoAtual)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "versao do aviso de privacidade invalida");
        }

        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.RegistrarConsentimentoAsync(
            id,
            requisicao.VersaoAvisoPrivacidade,
            DateTimeOffset.UtcNow,
            cancellationToken);

        return Ok(new ConsentimentoResponse(
            conversa.Id,
            conversa.LeadId,
            conversa.Lead.ConsentimentoEm!.Value,
            conversa.Lead.VersaoAvisoPrivacidade!));
    }

    /// <summary>Envia uma mensagem do lead e devolve a resposta da Lia.</summary>
    [HttpPost("{id:guid}/mensagens")]
    [EnableRateLimiting("mensagens")]
    [ProducesResponseType<MensagemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MensagemResponse>> Enviar(
        Guid id,
        NovaMensagemRequest requisicao,
        CancellationToken cancellationToken)
    {
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);

        if (conversa?.Lead.TemConsentimento != true)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "consentimento de privacidade pendente");
        }

        var agora = DateTimeOffset.UtcNow;
        var historico = await conversas.HistoricoRecenteAsync(id, Janela, cancellationToken);
        var horariosOferecidos = await agenda.OfertarAsync(id, agora, cancellationToken);

        var turno = new TurnoRequest(
            id,
            requisicao.Texto,
            historico,
            conversa.Lead.ParaContrato(),
            horariosOferecidos);

        var inicio = Stopwatch.GetTimestamp();
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
            var latenciaFalha = Stopwatch.GetElapsedTime(inicio).TotalMilliseconds;
            logger.LogError(erro, "Turno da conversa {ConversaId} falhou apos {LatenciaMs:F1}ms", id, latenciaFalha);

            return this.Traduzir(erro, environment);
        }

        var latenciaMs = Stopwatch.GetElapsedTime(inicio).TotalMilliseconds;
        logger.LogInformation(
            "Turno concluido para conversa {ConversaId}. Intencao: {Intencao}, ProximaAcao: {ProximaAcao}, ImoveisSugeridos: {QtdImoveis}, LatenciaMs: {LatenciaMs:F1}",
            id, resposta.Intencao, resposta.ProximaAcao, resposta.ImoveisSugeridos.Count, latenciaMs);

        var gravadoEm = DateTimeOffset.UtcNow;

        conversas.AplicarTurno(conversa, requisicao.Texto, resposta, gravadoEm);

        var atribuicao = await encaminhamentos.DecidirAsync(
            conversa, resposta.ProximaAcao, gravadoEm, cancellationToken);

        var agendamento = await gravacao.SalvarAsync(
            conversa,
            atribuicao.Novo,
            horariosOferecidos,
            resposta.SlotEscolhido,
            gravadoEm,
            cancellationToken);

        return Ok(new MensagemResponse(
            id,
            resposta.Resposta,
            resposta.Intencao,
            resposta.ProximaAcao,
            conversa.Lead.ParaContrato(),
            resposta.ImoveisSugeridos,
            atribuicao.Corretor,
            !conversa.Lead.TemContato,
            agendamento));
    }

    /// <summary>Grava nome e contato do lead no momento do handoff.</summary>
    [HttpPost("{id:guid}/contato")]
    [ProducesResponseType<ContatoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ContatoResponse>> RegistrarContato(
        Guid id,
        ContatoRequest requisicao,
        CancellationToken cancellationToken)
    {
        if (Contato.Telefone(requisicao.Telefone) is null && Contato.Email(requisicao.Email) is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "informe telefone ou e-mail");
        }

        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);

        if (conversa is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        var leadId = await conversas.RegistrarContatoAsync(
            conversa, requisicao, DateTimeOffset.UtcNow, cancellationToken);

        return Ok(new ContatoResponse(leadId));
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

        var corretor = await encaminhamentos.CorretorDaConversaAsync(id, cancellationToken);
        var agendaAtual = await agenda.OfertarAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        var mensagens = await conversas.HistoricoCompletoAsync(
            id, corretor, agendaAtual, cancellationToken);

        return Ok(new ConversaResponse(
            id,
            conversa.Lead.ParaContrato(),
            mensagens,
            !conversa.Lead.TemContato,
            conversa.Lead.ConsentimentoEm,
            conversa.Lead.VersaoAvisoPrivacidade));
    }

    /// <summary>
    /// Exclui uma conversa especifica. Por padrao (excluirLead=false), remove apenas a
    /// conversa e suas mensagens/encaminhamentos, preservando o lead e eventuais outras
    /// conversas. Se excluirLead=true for solicitado (direito de eliminacao LGPD completo),
    /// exige autorizacao de privacidade e elimina o lead e todos os seus vinculos em cascata.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("exclusao")]
    [ProducesResponseType<ExclusaoConversaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ExclusaoConversaResponse>> Excluir(
        Guid id,
        [FromQuery] bool excluirLead = false,
        CancellationToken cancellationToken = default)
    {
        var erroAuth = AutorizacaoPrivacidade.Validar(Request, configuracao, this);
        if (erroAuth is not null)
        {
            return erroAuth;
        }

        if (excluirLead)
        {
            var conversa = await conversas.ObterAsync(id, cancellationToken);
            if (conversa is null)
            {
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
            }

            var conversaIds = await conversas.ObterIdsDeConversasDoLeadAsync(conversa.LeadId, cancellationToken);
            var idsParaTravar = conversaIds.Append(conversa.LeadId);

            using var _ = await travas.TravarMultiplasAsync(idsParaTravar, cancellationToken);

            var resultadoLead = await conversas.ExcluirLeadAsync(conversa.LeadId, cancellationToken);
            if (resultadoLead is null)
            {
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa ou lead nao encontrado");
            }

            logger.LogInformation(
                "Conversa {ConversaId} e lead {LeadId} foram eliminados por solicitacao LGPD completa",
                id, resultadoLead.LeadId);

            return Ok(new ExclusaoConversaResponse(
                id,
                resultadoLead.LeadId,
                LeadExcluido: true,
                resultadoLead.MensagensExcluidas,
                resultadoLead.RemovidoEm,
                Escopo: "lead_e_vinculos",
                Mensagem: "Lead e todas as conversas/registros vinculados foram eliminados definitivamente."));
        }
        else
        {
            using var _ = await travas.TravarAsync(id, cancellationToken);

            var resultado = await conversas.ExcluirApenasConversaAsync(id, cancellationToken);
            if (resultado is null)
            {
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
            }

            logger.LogInformation(
                "Conversa {ConversaId} excluida. Lead {LeadId} preservado",
                id, resultado.LeadId);

            return Ok(new ExclusaoConversaResponse(
                id,
                resultado.LeadId,
                LeadExcluido: false,
                resultado.MensagensExcluidas,
                resultado.RemovidoEm,
                Escopo: "apenas_conversa",
                Mensagem: "Conversa e suas mensagens foram removidas. O lead e outras conversas permanecem preservados."));
        }
    }
}
