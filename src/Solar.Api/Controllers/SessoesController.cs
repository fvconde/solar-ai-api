using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Controllers;

[ApiController]
[Produces("application/json")]
public sealed class SessoesController(
    SolarDbContext db,
    IPasswordHasher<Corretor> passwordHasher,
    ConversaRepositorio conversas,
    TimeProvider timeProvider,
    IHostEnvironment environment) : ControllerBase
{
    private static readonly TimeSpan DuracaoBloqueio = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TravasLogin = new(StringComparer.Ordinal);

    [HttpPost("/api/sessoes")]
    [AllowAnonymous]
    [EnableRateLimiting("painel")]
    [ProducesResponseType<SessaoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<SessaoResponse>> CriarAsync(
        [FromBody] SessaoRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || !NormalizadorDeEmail.TentarNormalizar(request.Email, out var emailNormalizado))
        {
            return Unauthorized(new ErroApiResponse("credenciais_invalidas", "As credenciais não são válidas."));
        }

        var trava = TravasLogin.GetOrAdd(emailNormalizado, _ => new SemaphoreSlim(1, 1));
        await trava.WaitAsync(cancellationToken);
        try
        {
            var conta = await db.Corretores
                .SingleOrDefaultAsync(c => c.EmailNormalizado == emailNormalizado, cancellationToken);
            if (conta is null)
            {
                return Unauthorized(new ErroApiResponse("credenciais_invalidas", "As credenciais não são válidas."));
            }

            var agora = timeProvider.GetUtcNow();
            if (conta.BloqueadoAte is { } bloqueadoAte)
            {
                if (bloqueadoAte > agora)
                {
                    return StatusCode(StatusCodes.Status423Locked, new BloqueadoResponse(
                        "bloqueado", SegundosRestantes(bloqueadoAte, agora)));
                }
                conta.LimparBloqueioExpirado();
            }

            var verificacao = conta.SenhaHash is null
                ? PasswordVerificationResult.Failed
                : passwordHasher.VerifyHashedPassword(conta, conta.SenhaHash, request.Senha ?? string.Empty);

            if (verificacao == PasswordVerificationResult.Failed)
            {
                conta.RegistrarFalhaDeSenha(agora, DuracaoBloqueio);
                await db.SaveChangesAsync(cancellationToken);
                if (conta.BloqueadoAte is { } novoBloqueio && novoBloqueio > agora)
                {
                    return StatusCode(StatusCodes.Status423Locked, new BloqueadoResponse(
                        "bloqueado", SegundosRestantes(novoBloqueio, agora)));
                }
                return Unauthorized(new ErroApiResponse("credenciais_invalidas", "As credenciais não são válidas."));
            }

            if (verificacao == PasswordVerificationResult.SuccessRehashNeeded)
            {
                conta.DefinirSenhaHash(passwordHasher.HashPassword(conta, request.Senha ?? string.Empty));
            }
            conta.RegistrarAcertoDeSenha();

            if (!conta.Ativo)
            {
                await db.SaveChangesAsync(cancellationToken);
                return Unauthorized(new ErroApiResponse("credenciais_invalidas", "As credenciais não são válidas."));
            }

            if (conta.Perfil == PerfisDoPainel.Cliente && request.ConversaId is { } conversaId)
            {
                await conversas.VincularContaAsync(conversaId, conta.Id, agora, cancellationToken);
            }

            var token = TokenSeguro.Criar();
            var sessao = SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), agora, agora.AddDays(30));
            db.Sessoes.Add(sessao);
            await db.SaveChangesAsync(cancellationToken);
            DefinirCookie(token, sessao.ExpiraEm);

            return Ok(await ParaContratoAsync(conta, cancellationToken));
        }
        finally
        {
            trava.Release();
        }
    }

    [HttpGet("/api/sessao")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<SessaoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessaoResponse>> ObterAsync(CancellationToken cancellationToken)
    {
        var id = ObterContaId();
        if (id is null)
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }

        var conta = await db.Corretores.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id && c.Ativo, cancellationToken);
        return conta is null
            ? Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."))
            : Ok(await ParaContratoAsync(conta, cancellationToken));
    }

    [HttpDelete("/api/sessao")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExcluirAsync(CancellationToken cancellationToken)
    {
        var sessaoId = User.FindFirstValue(CorretorAuthenticationDefaults.SessaoIdClaim);
        if (!Guid.TryParse(sessaoId, out var id))
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }

        var sessao = await db.Sessoes.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (sessao is not null)
        {
            sessao.Revogar(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        Response.Cookies.Delete(CorretorAuthenticationDefaults.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !environment.IsDevelopment(),
            Path = "/",
        });
        return NoContent();
    }

    private Guid? ObterContaId()
    {
        var valor = User.FindFirstValue(CorretorAuthenticationDefaults.CorretorIdClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(valor, out var id) ? id : null;
    }

    private async Task<SessaoResponse> ParaContratoAsync(Corretor conta, CancellationToken cancellationToken)
    {
        var supervisor = conta.Perfil == PerfisDoPainel.Supervisor;
        var corretor = conta.Perfil == PerfisDoPainel.Corretor;
        IReadOnlyList<string> filtros = supervisor
            ? conta.VinculoAtivo
                ? ["minha_fila", "sem_corretor", "visao_geral"]
                : ["sem_corretor", "visao_geral"]
            : corretor ? ["meus_leads"] : [];
        var filtroInicial = filtros.Count == 0
            ? null
            : supervisor ? conta.VinculoAtivo ? "minha_fila" : "sem_corretor" : "meus_leads";
        int? pendentes = supervisor
            ? await db.Corretores.CountAsync(c => c.StatusCorretor == StatusDoCorretor.EmAnalise, cancellationToken)
            : null;
        Guid? corretorId = corretor || (supervisor && conta.VinculoAtivo) ? conta.Id : null;

        return new SessaoResponse(
            new UsuarioResponse(conta.Id, conta.Nome, conta.Email),
            conta.Perfil,
            conta.Perfil == PerfisDoPainel.Cliente ? null : conta.StatusCorretor,
            corretorId,
            conta.VinculoAtivo,
            filtros,
            filtroInicial,
            pendentes);
    }

    private void DefinirCookie(string token, DateTimeOffset expiraEm) => Response.Cookies.Append(
        CorretorAuthenticationDefaults.CookieName,
        token,
        new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !environment.IsDevelopment(),
            Path = "/",
            MaxAge = TimeSpan.FromDays(30),
            Expires = expiraEm,
        });

    private static int SegundosRestantes(DateTimeOffset bloqueadoAte, DateTimeOffset agora) =>
        Math.Clamp((int)Math.Ceiling((bloqueadoAte - agora).TotalSeconds), 1, (int)DuracaoBloqueio.TotalSeconds);
}

public sealed record BloqueadoResponse(string Codigo, int SegundosRestantes);
