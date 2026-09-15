using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Controllers;

[ApiController]
[Route("painel")]
[EnableRateLimiting("painel")]
[Produces("application/json")]
public class PainelController : ControllerBase
{
    private const int TentativasMaximas = 5;
    private static readonly TimeSpan DuracaoBloqueio = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ValidadeRecuperacao = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan JanelaRateLimit = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TravasLogin = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TravasRecuperacao = new(StringComparer.Ordinal);

    private readonly SolarDbContext db;
    private readonly IConfiguration configuracao;
    private readonly IPasswordHasher<Corretor> passwordHasher;
    private readonly IEnviadorEmail enviadorEmail;
    private readonly IPainelRateLimitStore rateLimitStore;
    private readonly TimeProvider timeProvider;
    private readonly IHostEnvironment ambiente;

    public PainelController(
        SolarDbContext db,
        IConfiguration configuracao,
        IPasswordHasher<Corretor> passwordHasher,
        IEnviadorEmail enviadorEmail,
        IPainelRateLimitStore rateLimitStore,
        TimeProvider timeProvider,
        IHostEnvironment ambiente)
    {
        this.db = db;
        this.configuracao = configuracao;
        this.passwordHasher = passwordHasher;
        this.enviadorEmail = enviadorEmail;
        this.rateLimitStore = rateLimitStore;
        this.timeProvider = timeProvider;
        this.ambiente = ambiente;
    }

    [HttpPost("identificacao")]
    [AllowAnonymous]
    [ProducesResponseType<IdentificacaoPainelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroPainelResponse>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IdentificacaoPainelResponse>> IdentificarAsync(
        [FromBody] IdentificacaoPainelRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || !NormalizadorDeEmail.TentarNormalizar(request.Email, out var emailNormalizado))
        {
            return BadRequest(new ErroPainelResponse("formato"));
        }

        var cadastrado = await db.Corretores
            .AsNoTracking()
            .AnyAsync(c => c.EmailNormalizado == emailNormalizado, cancellationToken);

        return Ok(new IdentificacaoPainelResponse(cadastrado));
    }

    [HttpPost("sessoes")]
    [AllowAnonymous]
    [ProducesResponseType<SessaoPainelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<TentativasRestantesResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<BloqueadoPorSegundosResponse>(StatusCodes.Status423Locked)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SessaoPainelResponse>> CriarSessaoAsync(
        [FromBody] SessaoPainelRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || !NormalizadorDeEmail.TentarNormalizar(request.Email, out var emailNormalizado))
        {
            return Unauthorized(new TentativasRestantesResponse(TentativasMaximas));
        }

        var trava = TravasLogin.GetOrAdd(emailNormalizado, _ => new SemaphoreSlim(1, 1));
        await trava.WaitAsync(cancellationToken);

        try
        {
            var corretor = await db.Corretores
                .SingleOrDefaultAsync(c => c.EmailNormalizado == emailNormalizado, cancellationToken);

            if (corretor is null)
            {
                return Unauthorized(new TentativasRestantesResponse(TentativasMaximas));
            }

            var agora = Agora;

            if (corretor.BloqueadoAte is { } bloqueadoAte)
            {
                if (bloqueadoAte > agora)
                {
                    return StatusCode(
                        StatusCodes.Status423Locked,
                        new BloqueadoPorSegundosResponse(SegundosRestantes(bloqueadoAte, agora)));
                }

                corretor.LimparBloqueioExpirado();
            }

            var verificacao = corretor.SenhaHash is null
                ? PasswordVerificationResult.Failed
                : passwordHasher.VerifyHashedPassword(corretor, corretor.SenhaHash, request.Senha ?? string.Empty);

            if (verificacao is PasswordVerificationResult.Failed)
            {
                corretor.RegistrarFalhaDeSenha(agora, DuracaoBloqueio);
                await db.SaveChangesAsync(cancellationToken);

                if (corretor.BloqueadoAte is { } novoBloqueio && novoBloqueio > agora)
                {
                    return StatusCode(
                        StatusCodes.Status423Locked,
                        new BloqueadoPorSegundosResponse(SegundosRestantes(novoBloqueio, agora)));
                }

                return Unauthorized(new TentativasRestantesResponse(
                    Math.Max(0, TentativasMaximas - corretor.TentativasSenha)));
            }

            if (verificacao is PasswordVerificationResult.SuccessRehashNeeded)
            {
                corretor.DefinirSenhaHash(passwordHasher.HashPassword(corretor, request.Senha ?? string.Empty));
            }

            corretor.RegistrarAcertoDeSenha();

            if (!corretor.Ativo)
            {
                await db.SaveChangesAsync(cancellationToken);
                return Forbid();
            }

            var token = TokenSeguro.Criar();
            db.Sessoes.Add(SessaoCorretor.Nova(corretor.Id, TokenSeguro.Sha256(token), agora));
            await db.SaveChangesAsync(cancellationToken);

            DefinirCookieDeSessao(token);
            return Ok(new SessaoPainelResponse(ParaContrato(corretor)));
        }
        finally
        {
            trava.Release();
        }
    }

    [HttpGet("sessao")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<SessaoPainelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessaoPainelResponse>> ObterSessaoAsync(
        CancellationToken cancellationToken)
    {
        var corretorId = ObterCorretorIdDaSessao();

        if (corretorId is null)
        {
            return Unauthorized();
        }

        var corretor = await db.Corretores
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == corretorId && c.Ativo, cancellationToken);

        return corretor is null
            ? Unauthorized()
            : Ok(new SessaoPainelResponse(ParaContrato(corretor)));
    }

    [HttpPost("senha/recuperacoes")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SolicitarRecuperacaoAsync(
        [FromBody] RecuperacaoSenhaRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || !NormalizadorDeEmail.TentarNormalizar(request.Email, out var emailNormalizado))
        {
            return Accepted();
        }

        var agora = Agora;

        if (!PodeSolicitarRecuperacao(emailNormalizado, agora))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var trava = TravasRecuperacao.GetOrAdd(emailNormalizado, _ => new SemaphoreSlim(1, 1));
        await trava.WaitAsync(cancellationToken);

        try
        {
            var corretor = await db.Corretores
                .SingleOrDefaultAsync(c => c.EmailNormalizado == emailNormalizado, cancellationToken);

            if (corretor is null || !corretor.Ativo || string.IsNullOrWhiteSpace(corretor.Email))
            {
                return Accepted();
            }

            var anteriores = await db.RecuperacoesSenha
                .Where(r => r.CorretorId == corretor.Id && r.UsadaEm == null && r.InvalidadaEm == null)
                .ToListAsync(cancellationToken);

            foreach (var anterior in anteriores)
            {
                anterior.Invalidar(agora);
            }

            var token = TokenSeguro.Criar();
            db.RecuperacoesSenha.Add(RecuperacaoSenha.Nova(
                corretor.Id,
                TokenSeguro.Sha256(token),
                agora,
                agora.Add(ValidadeRecuperacao)));
            await db.SaveChangesAsync(cancellationToken);

            var link = MontarLinkDeRecuperacao(token);
            await enviadorEmail.EnviarLinkRecuperacaoAsync(corretor.Email, link, cancellationToken);

            return Accepted();
        }
        finally
        {
            trava.Release();
        }
    }

    [HttpGet("senha/recuperacoes/{token}")]
    [AllowAnonymous]
    [ProducesResponseType<RecuperacaoSenhaTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<RecuperacaoSenhaTokenResponse>> ValidarRecuperacaoAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var tokenHash = TokenSeguro.TentarCalcularSha256(token);

        if (tokenHash is null)
        {
            return StatusCode(StatusCodes.Status410Gone);
        }

        var recuperacao = await BuscarRecuperacaoAsync(tokenHash, cancellationToken, rastrear: false);

        if (recuperacao?.Corretor is null || !recuperacao.ValidaEm(Agora))
        {
            return StatusCode(StatusCodes.Status410Gone);
        }

        return Ok(new RecuperacaoSenhaTokenResponse(recuperacao.Corretor.Email));
    }

    [HttpPost("senha")]
    [AllowAnonymous]
    [ProducesResponseType<SessaoPainelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroPainelResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<SessaoPainelResponse>> RedefinirSenhaAsync(
        [FromBody] NovaSenhaPainelRequest? request,
        CancellationToken cancellationToken)
    {
        var tokenHash = TokenSeguro.TentarCalcularSha256(request?.Token);

        if (tokenHash is null)
        {
            return StatusCode(StatusCodes.Status410Gone);
        }

        var trava = TravasRecuperacao.GetOrAdd(
            $"hash:{Convert.ToHexString(tokenHash)}",
            _ => new SemaphoreSlim(1, 1));
        await trava.WaitAsync(cancellationToken);

        try
        {
            var recuperacao = await BuscarRecuperacaoAsync(tokenHash, cancellationToken, rastrear: true);

            if (recuperacao?.Corretor is null || !recuperacao.ValidaEm(Agora))
            {
                return StatusCode(StatusCodes.Status410Gone);
            }

            if (request!.NovaSenha is null || request.NovaSenha.Length < 8)
            {
                return BadRequest(new ErroPainelResponse("politica"));
            }

            await using var transacao = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            var agora = Agora;
            var corretor = recuperacao.Corretor;
            corretor.DefinirSenhaHash(passwordHasher.HashPassword(corretor, request.NovaSenha));
            corretor.RegistrarAcertoDeSenha();
            recuperacao.Consumir(agora);

            var sessoesAnteriores = await db.Sessoes
                .Where(s => s.CorretorId == corretor.Id && s.RevogadaEm == null)
                .ToListAsync(cancellationToken);

            foreach (var sessao in sessoesAnteriores)
            {
                sessao.Revogar(agora);
            }

            var tokenSessao = TokenSeguro.Criar();
            db.Sessoes.Add(SessaoCorretor.Nova(
                corretor.Id,
                TokenSeguro.Sha256(tokenSessao),
                agora));
            await db.SaveChangesAsync(cancellationToken);

            if (transacao is not null)
            {
                await transacao.CommitAsync(cancellationToken);
            }

            DefinirCookieDeSessao(tokenSessao);
            return Ok(new SessaoPainelResponse(ParaContrato(corretor)));
        }
        finally
        {
            trava.Release();
        }
    }

    [HttpGet("leads")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<FilaLeadsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FilaLeadsResponse>> ListarLeadsAsync(
        [FromQuery] string? intencao,
        [FromQuery] bool? meusLeads,
        CancellationToken cancellationToken)
    {
        var corretorId = ObterCorretorIdDaSessao();

        if (corretorId is null)
        {
            return Unauthorized();
        }

        var corretorAtivo = await db.Corretores
            .AsNoTracking()
            .AnyAsync(c => c.Id == corretorId && c.Ativo, cancellationToken);

        if (!corretorAtivo)
        {
            return Unauthorized();
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
                grupo => grupo.Key,
                grupo => grupo
                    .OrderByDescending(e => e.Em)
                    .ThenByDescending(e => e.Id)
                    .First());

        var resultado = new List<LeadPainelItem>();

        foreach (var lead in leadsList)
        {
            encaminhamentoPorLead.TryGetValue(lead.Id, out var encaminhamento);
            var leadCorretorId = encaminhamento?.CorretorId;
            var leadCorretorNome = encaminhamento?.Corretor?.Nome;

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

    private DateTimeOffset Agora => timeProvider.GetUtcNow();

    private async Task<RecuperacaoSenha?> BuscarRecuperacaoAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken,
        bool rastrear)
    {
        var consulta = db.RecuperacoesSenha.Include(r => r.Corretor);
        var recuperacao = rastrear
            ? await consulta.SingleOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken)
            : await consulta.AsNoTracking().SingleOrDefaultAsync(r => r.TokenHash == tokenHash, cancellationToken);

        if (recuperacao is null && db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            var todas = await (rastrear ? consulta : consulta.AsNoTracking()).ToListAsync(cancellationToken);
            recuperacao = todas.SingleOrDefault(r => TokenSeguro.HashesIguais(r.TokenHash, tokenHash));
        }

        return recuperacao;
    }

    private Guid? ObterCorretorIdDaSessao()
    {
        var valor = User.FindFirstValue(CorretorAuthenticationDefaults.CorretorIdClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(valor, out var id) ? id : null;
    }

    private bool PodeSolicitarRecuperacao(string emailNormalizado, DateTimeOffset agora)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var limiteIp = configuracao.GetValue("RateLimiting:PainelRecuperacaoPorIpPorMinuto", 10);
        var limiteEmail = configuracao.GetValue("RateLimiting:PainelRecuperacaoPorEmailPorMinuto", 5);

        var ipPermitido = rateLimitStore.TentarConsumir(
            $"ip:{ip}", limiteIp, JanelaRateLimit, agora);
        var emailPermitido = rateLimitStore.TentarConsumir(
            $"email:{emailNormalizado}", limiteEmail, JanelaRateLimit, agora);

        return ipPermitido && emailPermitido;
    }

    private void DefinirCookieDeSessao(string token)
    {
        Response.Cookies.Append(
            CorretorAuthenticationDefaults.CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Secure = !ambiente.IsDevelopment(),
                MaxAge = TimeSpan.FromDays(3650),
            });
    }

    private string MontarLinkDeRecuperacao(string token)
    {
        const string chaveUrlBaseDoFront = "Painel:UrlBaseDoFront";
        var urlBaseDoFront = configuracao[chaveUrlBaseDoFront];

        if (string.IsNullOrWhiteSpace(urlBaseDoFront)
            || !Uri.TryCreate(urlBaseDoFront, UriKind.Absolute, out var urlBaseDoFrontUri)
            || (urlBaseDoFrontUri.Scheme != Uri.UriSchemeHttp
                && urlBaseDoFrontUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(urlBaseDoFrontUri.Host)
            || !string.IsNullOrEmpty(urlBaseDoFrontUri.Query)
            || !string.IsNullOrEmpty(urlBaseDoFrontUri.Fragment))
        {
            throw new InvalidOperationException(
                $"{chaveUrlBaseDoFront} deve ser uma URL absoluta HTTP(S) sem query ou fragmento.");
        }

        return $"{urlBaseDoFront.TrimEnd('/')}/entrar?token={Uri.EscapeDataString(token)}";
    }

    private static int SegundosRestantes(DateTimeOffset bloqueadoAte, DateTimeOffset agora) =>
        Math.Clamp((int)Math.Ceiling((bloqueadoAte - agora).TotalSeconds), 1, (int)DuracaoBloqueio.TotalSeconds);

    private static CorretorPainelResponse ParaContrato(Corretor corretor) =>
        new(corretor.Id, corretor.Nome, corretor.Especialidade);

}
