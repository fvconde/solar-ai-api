using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Controllers;

[ApiController]
[Route("painel")]
[EnableRateLimiting("painel")]
[Produces("application/json")]
public class PainelController(
    SolarDbContext db,
    IConfiguration configuracao) : ControllerBase
{
    public const string HeaderCorretorId = "X-Corretor-Id";

    /// <summary>
    /// Lista os corretores ativos para a seleção de perfil no painel.
    /// Exige autorização de segurança/privacidade via cabeçalho X-Chave-Privacidade,
    /// X-Admin-Key ou Bearer token (falha fechada com 503 se chave não configurada).
    /// </summary>
    [HttpGet("corretores")]
    [ProducesResponseType<IReadOnlyList<CorretorIdentificacao>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<CorretorIdentificacao>>> ListarCorretoresAsync(
        CancellationToken cancellationToken)
    {
        var erroAuth = AutorizacaoPrivacidade.Validar(Request, configuracao, this);
        if (erroAuth is not null)
        {
            return erroAuth;
        }

        var corretores = await db.Corretores
            .AsNoTracking()
            .Where(c => c.Ativo)
            .OrderBy(c => c.Nome)
            .Select(c => new CorretorIdentificacao(c.Id, c.Nome, c.Especialidade))
            .ToListAsync(cancellationToken);

        return Ok(corretores);
    }

    /// <summary>
    /// Fila de leads com ordenação por score decrescente e filtros.
    /// Exige autorização de segurança/privacidade (X-Chave-Privacidade) E
    /// identificação de corretor ativo via cabeçalho X-Corretor-Id.
    /// Sem autorização ou identificação, devolve erro 401/403 e nenhum dado.
    /// </summary>
    [HttpGet("leads")]
    [ProducesResponseType<FilaLeadsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<FilaLeadsResponse>> ListarLeadsAsync(
        [FromHeader(Name = HeaderCorretorId)] string? corretorIdHeader,
        [FromQuery] string? intencao,
        [FromQuery] bool? meusLeads,
        CancellationToken cancellationToken)
    {
        var erroAuth = AutorizacaoPrivacidade.Validar(Request, configuracao, this);
        if (erroAuth is not null)
        {
            return erroAuth;
        }

        if (string.IsNullOrWhiteSpace(corretorIdHeader) ||
            !Guid.TryParse(corretorIdHeader, out var corretorId))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Identificação de corretor ativo obrigatória via cabeçalho X-Corretor-Id.");
        }

        var corretorExiste = await db.Corretores
            .AsNoTracking()
            .AnyAsync(c => c.Id == corretorId && c.Ativo, cancellationToken);

        if (!corretorExiste)
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Corretor não encontrado ou inativo.");
        }

        var leadsQuery = db.Leads.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(intencao))
        {
            leadsQuery = leadsQuery.Where(l => l.Intencao == intencao);
        }

        var leadsList = await leadsQuery
            .OrderByDescending(l => l.Score ?? 0)
            .ThenByDescending(l => l.AtualizadoEm)
            .ToListAsync(cancellationToken);

        var leadIds = leadsList.Select(l => l.Id).ToList();

        var encaminhamentos = await db.Encaminhamentos
            .AsNoTracking()
            .Include(e => e.Corretor)
            .Where(e => leadIds.Contains(e.LeadId))
            .ToListAsync(cancellationToken);

        var encaminhamentoPorLead = encaminhamentos
            .GroupBy(e => e.LeadId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.Em).First());

        var resultado = new List<LeadPainelItem>();
        foreach (var lead in leadsList)
        {
            encaminhamentoPorLead.TryGetValue(lead.Id, out var enc);
            var leadCorretorId = enc?.CorretorId;
            var leadCorretorNome = enc?.Corretor?.Nome;

            if (meusLeads == true && leadCorretorId != corretorId)
            {
                continue;
            }

            resultado.Add(new LeadPainelItem(
                lead.Id,
                lead.Nome,
                lead.Intencao,
                lead.Score,
                lead.AtualizadoEm,
                lead.Status,
                leadCorretorId,
                leadCorretorNome,
                lead.Regiao));
        }

        return Ok(new FilaLeadsResponse(resultado, resultado.Count));
    }
}
