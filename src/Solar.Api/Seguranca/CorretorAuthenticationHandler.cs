using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Microsoft.Extensions.Options;
using Solar.Api.Persistencia;

namespace Solar.Api.Seguranca;

public static class CorretorAuthenticationDefaults
{
    public const string AuthenticationScheme = "SolarCorretor";
    public const string CookieName = "solar_corretor_session";
    public const string CorretorIdClaim = "solar_corretor_id";
    public const string EspecialidadeClaim = "solar_corretor_especialidade";
    public const string SessaoIdClaim = "solar_sessao_id";
}

/// <summary>
/// Converte o cookie opaco em claims somente depois de consultar a sessao no
/// banco. O handler nunca registra o cookie nem o digest.
/// </summary>
public sealed class CorretorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SolarDbContext db,
    TimeProvider timeProvider) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[CorretorAuthenticationDefaults.CookieName];

        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var tokenHash = TokenSeguro.TentarCalcularSha256(token);

        if (tokenHash is null)
        {
            return AuthenticateResult.Fail("sessao invalida");
        }

        var sessao = await db.Sessoes
            .Include(s => s.Corretor)
            .SingleOrDefaultAsync(
                s => s.TokenHash == tokenHash && s.RevogadaEm == null,
                Context.RequestAborted);

        // O provider de testes InMemory compara arrays por referencia. O
        // fallback mantem a mesma semantica de hash sem alterar a consulta
        // eficiente do Postgres.
        if (sessao is null && db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            sessao = (await db.Sessoes
                .Include(s => s.Corretor)
                    .Where(s => s.RevogadaEm == null)
                    .ToListAsync(Context.RequestAborted))
                .SingleOrDefault(s => TokenSeguro.HashesIguais(s.TokenHash, tokenHash));
        }

        var agora = timeProvider.GetUtcNow();
        if (sessao?.Corretor is not { Ativo: true } corretor || sessao.ExpiraEm <= agora)
        {
            return AuthenticateResult.Fail("sessao invalida");
        }

        sessao.Renovar(agora.AddDays(30));
        await db.SaveChangesAsync(Context.RequestAborted);
        Response.Cookies.Append(
            CorretorAuthenticationDefaults.CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = Context.RequestServices.GetService<IHostEnvironment>()?.IsDevelopment() != true,
                Path = "/",
                MaxAge = TimeSpan.FromDays(30),
                Expires = sessao.ExpiraEm,
            });

        var id = corretor.Id.ToString("D");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(CorretorAuthenticationDefaults.CorretorIdClaim, id),
            new Claim(CorretorAuthenticationDefaults.SessaoIdClaim, sessao.Id.ToString("D")),
            new Claim(ClaimTypes.Name, corretor.Nome),
            new Claim(CorretorAuthenticationDefaults.EspecialidadeClaim, corretor.Especialidade),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
    }
}
