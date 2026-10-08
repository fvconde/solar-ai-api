using System.Diagnostics;
using System.Security.Claims;
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
    public const string NomeCookieChaveExclusao = "solar.chave_exclusao";

    private const int JanelaPadrao = 20;

    private int Janela => Math.Clamp(
        configuracao.GetValue("Conversas:JanelaHistorico", JanelaPadrao),
        2,
        ContratoTurno.LimiteHistorico);

    [HttpPost("{id:guid}/consentimento")]
    [EnableRateLimiting("mensagens")]
    [ProducesResponseType<ConsentimentoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConsentimentoResponse>> RegistrarConsentimento(
        Guid id,
        ConsentimentoRequest requisicao,
        CancellationToken cancellationToken)
    {
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var existente = await conversas.ObterAsync(id, cancellationToken);
        if (existente is not null && !PodeAcessar(existente))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        if (requisicao.VersaoAvisoPrivacidade != AvisoPrivacidade.VersaoAtual)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "versao do aviso de privacidade invalida");
        }

        var contaId = ContaClienteAutenticada();
        var chave = existente is null && contaId is null
            ? TokenSeguro.Criar()
            : null;
        var hash = chave is null ? null : TokenSeguro.Sha256(chave);
        var conversa = await conversas.RegistrarConsentimentoAsync(
            id,
            requisicao.VersaoAvisoPrivacidade,
            DateTimeOffset.UtcNow,
            cancellationToken,
            contaId,
            hash);

        if (chave is not null && hash is not null
            && conversa.ContaId is null
            && TokenSeguro.HashesIguais(conversa.ChaveExclusaoHash, hash))
        {
            Response.Cookies.Append(NomeCookieChaveExclusao, chave, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = !environment.IsDevelopment(),
                Path = $"/conversas/{conversa.Id:D}",
            });
        }

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
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MensagemResponse>> Enviar(
        Guid id,
        NovaMensagemRequest requisicao,
        CancellationToken cancellationToken)
    {
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);
        if (conversa is not null && !PodeAcessar(conversa))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

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
            horariosOferecidos,
            conversa.Lead.TemContato,
            await agenda.TemAgendamentoConfirmadoAsync(id, cancellationToken));

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
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);

        if (conversa is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        if (!PodeAcessar(conversa))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        if (Contato.Telefone(requisicao.Telefone) is null && Contato.Email(requisicao.Email) is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "informe telefone ou e-mail");
        }

        var agora = DateTimeOffset.UtcNow;
        var leadId = await conversas.RegistrarContatoAsync(
            conversa, requisicao, agora, cancellationToken);

        var oferta = await OfertarAgendamentoAsync(conversa, agora, cancellationToken);

        return Ok(new ContatoResponse(leadId, oferta));
    }

    [HttpPost("{id:guid}/agendamentos")]
    [ProducesResponseType<AgendamentoDaConversa>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AgendamentoDaConversa>> RegistrarAgendamento(
        Guid id,
        AgendamentoRequest requisicao,
        CancellationToken cancellationToken)
    {
        using var _ = await travas.TravarAsync(id, cancellationToken);

        var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);

        if (conversa is null || !PodeAcessar(conversa))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        if (await agenda.TemAgendamentoConfirmadoAsync(id, cancellationToken))
        {
            return Conflito("agendamento_ja_confirmado", "agendamento ja confirmado");
        }

        if (!conversa.Lead.TemContato)
        {
            return Conflito("contato_pendente", "contato pendente");
        }

        var corretor = await encaminhamentos.CorretorDaConversaAsync(id, cancellationToken);
        if (corretor is null)
        {
            return Conflito("corretor_nao_atribuido", "corretor nao atribuido");
        }

        var horario = await agenda.ObterHorarioDaConversaAsync(id, requisicao.SlotId, cancellationToken);
        if (horario is null)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "horario nao encontrado");
        }

        var agora = DateTimeOffset.UtcNow;
        var agendamento = await gravacao.SalvarAgendamentoPorBotaoAsync(
            conversa, horario, corretor, agora, cancellationToken);

        if (agendamento.Estado == EstadosDoAgendamento.Confirmado)
        {
            return Ok(agendamento);
        }

        return Conflito("horario_indisponivel", "horario indisponivel", agendamento.Alternativas);
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

        if (!PodeAcessar(conversa))
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        }

        var agora = DateTimeOffset.UtcNow;
        var corretor = await encaminhamentos.CorretorDaConversaAsync(id, cancellationToken);
        var agendaAtual = await agenda.OfertarAsync(id, agora, cancellationToken);
        var mensagens = await conversas.HistoricoCompletoAsync(
            id, corretor, agendaAtual, cancellationToken);
        var oferta = await OfertarAgendamentoAsync(conversa, agora, cancellationToken);

        return Ok(new ConversaResponse(
            id,
            conversa.Lead.ParaContrato(),
            mensagens,
            !conversa.Lead.TemContato,
            conversa.Lead.ConsentimentoEm,
            conversa.Lead.VersaoAvisoPrivacidade,
            oferta));
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

    [HttpDelete("{id:guid}/titular")]
    [EnableRateLimiting("exclusao")]
    [ProducesResponseType<ExclusaoTitularResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ExclusaoTitularResponse>> ExcluirPeloTitular(
        Guid id, CancellationToken cancellationToken)
    {
        var prova = new ProvaExclusaoTitular(ContaAutenticada(),
            TokenSeguro.TentarCalcularSha256(Request.Cookies[NomeCookieChaveExclusao]));
        var conversa = await conversas.ObterAsync(id, cancellationToken);
        if (conversa is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        if (!prova.Autoriza(conversa))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "exclusao nao autorizada");

        var vinculadas = await conversas.ObterIdsDeConversasDoLeadAsync(conversa.LeadId, cancellationToken);
        using var trava = await travas.TravarMultiplasAsync(
            vinculadas.Append(id).Append(conversa.LeadId), cancellationToken);
        var resultado = await conversas.ExcluirPeloTitularAsync(
            id, conversa.LeadId, vinculadas, prova, cancellationToken);
        if (resultado.Estado == EstadoExclusaoTitular.NaoEncontrada)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "conversa nao encontrada");
        if (resultado.Estado == EstadoExclusaoTitular.NaoAutorizada)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "exclusao nao autorizada");
        if (resultado.Estado == EstadoExclusaoTitular.Conflito)
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "conversa alterada durante a solicitacao; tente novamente");

        Response.Cookies.Delete(NomeCookieChaveExclusao, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !environment.IsDevelopment(),
            Path = $"/conversas/{id:D}",
        });
        return Ok(new ExclusaoTitularResponse(
            resultado.LeadExcluido,
            resultado.RemovidoEm!.Value,
            resultado.LeadExcluido ? "lead_e_vinculos" : "apenas_conversa",
            "A conversa e suas mensagens foram apagadas definitivamente."));
    }

    private Guid? ContaAutenticada()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var valor = User.FindFirstValue(CorretorAuthenticationDefaults.CorretorIdClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(valor, out var id) ? id : null;
    }

    private Guid? ContaClienteAutenticada() =>
        User.FindFirstValue(CorretorAuthenticationDefaults.PerfilClaim) == PerfisDoPainel.Cliente
            ? ContaAutenticada()
            : null;

    private bool PodeAcessar(Conversa conversa) =>
        conversa.ContaId is null || ContaAutenticada() == conversa.ContaId;

    private async Task<IReadOnlyList<SlotOferecido>> OfertarAgendamentoAsync(
        Conversa conversa,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        if (!conversa.Lead.TemContato)
        {
            return [];
        }

        if (await agenda.TemAgendamentoConfirmadoAsync(conversa.Id, cancellationToken))
        {
            return [];
        }

        return await agenda.OfertarAsync(conversa.Id, agora, cancellationToken);
    }

    private ObjectResult Conflito(string codigo, string titulo, IReadOnlyList<SlotOferecido>? oferta = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = titulo,
            Type = "about:blank"
        };
        problem.Extensions["codigo"] = codigo;
        if (oferta is not null)
        {
            problem.Extensions["oferta"] = oferta;
        }

        return StatusCode(StatusCodes.Status409Conflict, problem);
    }
}
