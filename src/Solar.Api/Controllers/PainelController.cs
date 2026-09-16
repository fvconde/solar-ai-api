using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
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
    private readonly ConversaRepositorio conversas;
    private readonly ILogger<PainelController> logger;

    public PainelController(
        SolarDbContext db,
        IConfiguration configuracao,
        IPasswordHasher<Corretor> passwordHasher,
        IEnviadorEmail enviadorEmail,
        IPainelRateLimitStore rateLimitStore,
        TimeProvider timeProvider,
        IHostEnvironment ambiente,
        ConversaRepositorio? conversas = null,
        ILogger<PainelController>? logger = null)
    {
        this.db = db;
        this.configuracao = configuracao;
        this.passwordHasher = passwordHasher;
        this.enviadorEmail = enviadorEmail;
        this.rateLimitStore = rateLimitStore;
        this.timeProvider = timeProvider;
        this.ambiente = ambiente;
        this.conversas = conversas ?? new ConversaRepositorio(db);
        this.logger = logger ?? NullLogger<PainelController>.Instance;
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
            return Ok(ParaContrato(corretor));
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
            return Unauthorized(new ErroPainelResponse("sessao_invalida"));
        }

        var corretor = await db.Corretores
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == corretorId && c.Ativo, cancellationToken);

        return corretor is null
            ? Unauthorized(new ErroPainelResponse("sessao_invalida"))
            : Ok(ParaContrato(corretor));
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
            return Ok(ParaContrato(corretor));
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
    [ProducesResponseType<PerfilInsuficientePainelResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<FilaLeadsResponse>> ListarLeadsAsync(
        [FromQuery] string? filtro,
        [FromQuery] string? intencao,
        CancellationToken cancellationToken)
    {
        var sessao = await ObterSessaoPainelAsync(cancellationToken);
        if (sessao is null)
        {
            return Unauthorized(new ErroPainelResponse("sessao_invalida"));
        }

        var filtroEfetivo = string.IsNullOrWhiteSpace(filtro)
            ? sessao.FiltroInicial
            : filtro.Trim();

        if (!sessao.FiltrosPermitidos.Contains(filtroEfetivo, StringComparer.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new PerfilInsuficientePainelResponse(
                    "perfil_insuficiente",
                    PerfisDoPainel.Supervisor));
        }

        if (sessao.Corretor.Perfil == PerfisDoPainel.Supervisor &&
            filtroEfetivo is "sem_corretor" or "visao_geral")
        {
            logger.LogInformation(
                "Acesso administrativo do painel por corretor {CorretorId} no filtro {Filtro}",
                sessao.Corretor.Id,
                filtroEfetivo);
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

        var autorizados = leadsList
            .Where(lead =>
            {
                encaminhamentoPorLead.TryGetValue(lead.Id, out var encaminhamento);
                return PertenceAoFiltro(
                    lead,
                    encaminhamento,
                    filtroEfetivo,
                    sessao.CorretorId);
            })
            .OrderByDescending(lead => lead.Score ?? 0)
            .ThenByDescending(lead => lead.AtualizadoEm)
            .ToList();

        var itens = autorizados
            .Select(lead =>
            {
                encaminhamentoPorLead.TryGetValue(lead.Id, out var encaminhamento);
                return new LeadPainelItem(
                    lead.Id,
                    lead.Nome,
                    ReferenciaDo(lead.Id),
                    PedidoResumoDe(lead),
                    lead.CriadoEm,
                    lead.Score,
                    lead.Status,
                    encaminhamento?.Status,
                    sessao.Corretor.Perfil == PerfisDoPainel.Supervisor
                        ? ParaResumo(encaminhamento?.Corretor)
                        : null);
            })
            .ToList();

        return Ok(new FilaLeadsResponse(itens, itens.Count));
    }

    [HttpGet("leads/{id:guid}")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<DetalheLeadPainelResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroPainelResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErroPainelResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DetalheLeadPainelResponse>> ObterDetalheLeadAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var sessao = await ObterSessaoPainelAsync(cancellationToken);
        if (sessao is null)
        {
            return Unauthorized(new ErroPainelResponse("sessao_invalida"));
        }

        var lead = await db.Leads
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (lead is null)
        {
            return NotFound(new ErroPainelResponse("lead_nao_encontrado"));
        }

        var encaminhamento = await db.Encaminhamentos
            .AsNoTracking()
            .Include(e => e.Corretor)
            .Where(e => e.LeadId == id)
            .OrderByDescending(e => e.Em)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var pertenceAoEscopo = sessao.Corretor.Perfil == PerfisDoPainel.Supervisor ||
            encaminhamento?.CorretorId == sessao.CorretorId;

        if (!pertenceAoEscopo)
        {
            return NotFound(new ErroPainelResponse("lead_nao_encontrado"));
        }

        if (sessao.Corretor.Perfil == PerfisDoPainel.Supervisor &&
            encaminhamento?.CorretorId != sessao.CorretorId)
        {
            logger.LogInformation(
                "Consulta administrativa do lead {LeadId} por corretor {CorretorId}",
                id,
                sessao.Corretor.Id);
        }

        var conversa = await db.Conversas
            .AsNoTracking()
            .Where(c => c.LeadId == id)
            .OrderByDescending(c => c.AtualizadaEm)
            .ThenByDescending(c => c.CriadaEm)
            .FirstOrDefaultAsync(cancellationToken);

        var transcricao = conversa is null
            ? []
            : await db.Mensagens
                .AsNoTracking()
                .Where(m => m.ConversaId == conversa.Id)
                .OrderBy(m => m.Em)
                .ThenBy(m => m.Id)
                .Select(m => new TranscricaoPainelResponse(
                    m.Papel == Papeis.Agente ? "lia" : "lead",
                    m.Texto,
                    m.Em))
                .ToListAsync(cancellationToken);

        var imoveisSugeridos = conversa is null
            ? []
            : await conversas.ObterImoveisSugeridosAsync(conversa.Id, cancellationToken);

        var slot = await db.Slots
            .AsNoTracking()
            .Where(s => s.LeadId == id)
            .OrderBy(s => s.Inicio)
            .Select(s => new { s.Inicio })
            .FirstOrDefaultAsync(cancellationToken);

        var fatores = FatoresDe(lead);
        int? valorQualificacao = fatores.Any(fator => fator.Preenchido)
            ? fatores.Where(fator => fator.Preenchido).Sum(fator => fator.Pontos)
            : null;

        var detalhe = new DetalheLeadPainelResponse(
            lead.Id,
            lead.Nome,
            ReferenciaDo(lead.Id),
            PedidoResumoDe(lead),
            lead.CriadoEm,
            lead.Status,
            new ContatoPainelResponse(lead.Telefone, lead.Email),
            new QualificacaoPainelResponse(valorQualificacao, fatores),
            encaminhamento?.Resumo,
            encaminhamento is null
                ? null
                : new EncaminhamentoPainelResponse(
                    encaminhamento.Id,
                    encaminhamento.Status,
                    ParaResumo(encaminhamento.Corretor),
                    encaminhamento.Em),
            slot is null
                ? null
                : new AgendamentoPainelResponse(slot.Inicio, EstadosDoAgendamento.Confirmado),
            imoveisSugeridos,
            transcricao);

        return Ok(detalhe);
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

    private async Task<SessaoPainelContext?> ObterSessaoPainelAsync(
        CancellationToken cancellationToken)
    {
        var corretorId = ObterCorretorIdDaSessao();
        if (corretorId is null)
        {
            return null;
        }

        var corretor = await db.Corretores
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == corretorId && c.Ativo, cancellationToken);

        if (corretor is null)
        {
            return null;
        }

        var (filtrosPermitidos, filtroInicial) = FiltrosPara(corretor);
        Guid? idDeEscopo = corretor.Perfil == PerfisDoPainel.Supervisor && !corretor.VinculoAtivo
            ? null
            : corretor.Id;

        return new SessaoPainelContext(corretor, idDeEscopo, filtrosPermitidos, filtroInicial);
    }

    private static (IReadOnlyList<string> FiltrosPermitidos, string FiltroInicial) FiltrosPara(
        Corretor corretor)
    {
        if (corretor.Perfil != PerfisDoPainel.Supervisor)
        {
            return (new[] { "meus_leads" }, "meus_leads");
        }

        return corretor.VinculoAtivo
            ? (new[] { "minha_fila", "sem_corretor", "visao_geral" }, "minha_fila")
            : (new[] { "sem_corretor", "visao_geral" }, "sem_corretor");
    }

    private static bool PertenceAoFiltro(
        Lead lead,
        Encaminhamento? encaminhamento,
        string filtro,
        Guid? corretorId)
    {
        return filtro switch
        {
            "meus_leads" or "minha_fila" =>
                corretorId is not null && encaminhamento?.CorretorId == corretorId,
            "sem_corretor" =>
                (lead.Status == StatusDoLead.Novo && encaminhamento is null) ||
                encaminhamento?.CorretorId is null,
            "visao_geral" => true,
            _ => false,
        };
    }

    private static string ReferenciaDo(Guid id) =>
        id.ToString("N")[..8].ToUpperInvariant();

    private static string PedidoResumoDe(Lead lead)
    {
        var partes = new List<string>(capacity: 3);

        if (!string.IsNullOrWhiteSpace(lead.Intencao))
        {
            partes.Add(lead.Intencao);
        }

        if (lead.Quartos is { } quartos)
        {
            partes.Add($"{quartos} quartos");
        }

        if (!string.IsNullOrWhiteSpace(lead.Regiao))
        {
            partes.Add(lead.Regiao);
        }

        return partes.Count == 0
            ? "Preferências ainda não informadas"
            : string.Join(", ", partes);
    }

    private static CorretorPainelResumo? ParaResumo(Corretor? corretor) =>
        corretor is null
            ? null
            : new CorretorPainelResumo(corretor.Id, corretor.Nome, IniciaisDe(corretor.Nome));

    private static string IniciaisDe(string nome)
    {
        var partes = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0)
        {
            return string.Empty;
        }

        if (partes.Length == 1)
        {
            return partes[0].Length == 1
                ? partes[0].ToUpperInvariant()
                : partes[0][..2].ToUpperInvariant();
        }

        return string.Concat(
            char.ToUpperInvariant(partes[0][0]),
            char.ToUpperInvariant(partes[^1][0]));
    }

    private static IReadOnlyList<FatorQualificacaoPainelResponse> FatoresDe(Lead lead) =>
    [
        new("finalidade", "Finalidade da busca", 15, !string.IsNullOrWhiteSpace(lead.Intencao)),
        new("bairro", "Bairro dentro da busca", 20, !string.IsNullOrWhiteSpace(lead.Regiao)),
        new("quartos", "Número de quartos", 15, lead.Quartos is not null),
        new("faixa", "Faixa de aluguel informada", 20, lead.PrecoMin is not null || lead.PrecoMax is not null),
        new("prazo", "Prazo de mudança", 10, !string.IsNullOrWhiteSpace(lead.Urgencia)),
        new("contato", "Contato confirmado para o corretor", 20, lead.TemContato),
    ];

    private static SessaoPainelResponse ParaContrato(Corretor corretor)
    {
        var (filtrosPermitidos, filtroInicial) = FiltrosPara(corretor);
        Guid? id = corretor.Perfil == PerfisDoPainel.Supervisor && !corretor.VinculoAtivo
            ? null
            : corretor.Id;

        return new SessaoPainelResponse(
            new CorretorPainelResponse(corretor.Id, corretor.Nome, corretor.Especialidade),
            corretor.Perfil,
            id,
            corretor.VinculoAtivo,
            filtrosPermitidos,
            filtroInicial);
    }

    private sealed record SessaoPainelContext(
        Corretor Corretor,
        Guid? CorretorId,
        IReadOnlyList<string> FiltrosPermitidos,
        string FiltroInicial);

}
