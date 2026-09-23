using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Controllers;

[ApiController]
[Produces("application/json")]
public sealed class ContasController(
    SolarDbContext db,
    IPasswordHasher<Corretor> passwordHasher,
    ConversaRepositorio conversas,
    TimeProvider timeProvider,
    IHostEnvironment environment) : ControllerBase
{
    [HttpPost("/api/contas")]
    [AllowAnonymous]
    [EnableRateLimiting("cadastros")]
    [ProducesResponseType<SessaoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidacaoResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<SessaoResponse>> CadastrarClienteAsync(
        [FromBody] CadastroContaRequest? request,
        CancellationToken cancellationToken) => request is null
        ? Task.FromResult<ActionResult<SessaoResponse>>(BadRequest(new ValidacaoResponse(
            "validacao", new Dictionary<string, string> { ["nome"] = "obrigatorio" })))
        : CadastrarAsync(
            request.Nome,
            request.Email,
            request.Telefone,
            request.Senha,
            request.AceitePrivacidade,
            request.ConversaId,
            PerfisDoPainel.Cliente,
            [],
            [],
            cancellationToken);

    [HttpPost("/api/corretores")]
    [AllowAnonymous]
    [EnableRateLimiting("cadastros")]
    [ProducesResponseType<SessaoResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidacaoResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<SessaoResponse>> CadastrarCorretorAsync(
        [FromBody] CadastroCorretorRequest? request,
        CancellationToken cancellationToken) => request is null
        ? Task.FromResult<ActionResult<SessaoResponse>>(BadRequest(new ValidacaoResponse(
            "validacao", new Dictionary<string, string> { ["nome"] = "obrigatorio" })))
        : CadastrarAsync(
            request.Nome,
            request.Email,
            request.Telefone,
            request.Senha,
            request.AceitePrivacidade,
            null,
            PerfisDoPainel.Corretor,
            request.Regioes ?? [],
            request.Especialidades ?? [],
            cancellationToken);

    private async Task<ActionResult<SessaoResponse>> CadastrarAsync(
        string? nome,
        string? email,
        string? telefone,
        string? senha,
        bool? aceitePrivacidade,
        Guid? conversaId,
        string perfil,
        IReadOnlyList<string> regioes,
        IReadOnlyList<string> especialidades,
        CancellationToken cancellationToken)
    {
        var campos = Validar(nome, email, telefone, senha, aceitePrivacidade, perfil, regioes, especialidades,
            out var emailNormalizado, out var telefoneNormalizado, out var regioesNormalizadas, out var especialidadesNormalizadas);
        if (campos.Count > 0)
        {
            return BadRequest(new ValidacaoResponse("validacao", campos));
        }

        if (await db.Corretores.AnyAsync(c => c.EmailNormalizado == emailNormalizado, cancellationToken))
        {
            return Conflict(new ErroApiResponse("email_em_uso", "O e-mail já está em uso."));
        }

        var agora = timeProvider.GetUtcNow();
        var conta = Corretor.NovaConta(
            nome!.Trim(),
            email!.Trim(),
            emailNormalizado!,
            telefoneNormalizado!,
            "placeholder",
            perfil,
            regioesNormalizadas,
            especialidadesNormalizadas,
            AvisoPrivacidade.VersaoAtual,
            agora);
        conta.DefinirSenhaHash(passwordHasher.HashPassword(conta, senha!));

        var token = TokenSeguro.Criar();
        var sessao = SessaoCorretor.Nova(conta.Id, TokenSeguro.Sha256(token), agora, agora.AddDays(30));
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Corretores.Add(conta);
        db.Sessoes.Add(sessao);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (perfil == PerfisDoPainel.Cliente && conversaId is { } idConversa)
            {
                await conversas.VincularContaAsync(idConversa, conta.Id, agora, cancellationToken);
            }
            await transacao.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException erro) when (EhConflitoDeEmail(erro))
        {
            return Conflict(new ErroApiResponse("email_em_uso", "O e-mail já está em uso."));
        }

        Response.Cookies.Append(
            CorretorAuthenticationDefaults.CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = !environment.IsDevelopment(),
                Path = "/",
                MaxAge = TimeSpan.FromDays(30),
                Expires = sessao.ExpiraEm,
            });

        return StatusCode(
            StatusCodes.Status201Created,
            await ProjecoesS44.SessaoAsync(db, conta, cancellationToken));
    }

    private static Dictionary<string, string> Validar(
        string? nome,
        string? email,
        string? telefone,
        string? senha,
        bool? aceitePrivacidade,
        string perfil,
        IReadOnlyList<string> regioes,
        IReadOnlyList<string> especialidades,
        out string? emailNormalizado,
        out string? telefoneNormalizado,
        out IReadOnlyList<string> regioesNormalizadas,
        out IReadOnlyList<string> especialidadesNormalizadas)
    {
        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        emailNormalizado = null;
        telefoneNormalizado = Contato.Telefone(telefone);
        regioesNormalizadas = NormalizarLista(regioes);
        especialidadesNormalizadas = NormalizarLista(especialidades);

        if (string.IsNullOrWhiteSpace(nome)) campos["nome"] = "obrigatorio";
        else if (nome.Trim().Length < 2) campos["nome"] = "curto";
        else if (nome.Trim().Length > 120) campos["nome"] = "longo";

        if (string.IsNullOrWhiteSpace(email)) campos["email"] = "obrigatorio";
        else if (!NormalizadorDeEmail.TentarNormalizar(email, out emailNormalizado)) campos["email"] = "formato";

        if (string.IsNullOrWhiteSpace(telefone)) campos["telefone"] = "obrigatorio";
        else if (telefoneNormalizado is null || telefoneNormalizado.Length is < 10 or > 11) campos["telefone"] = "formato";

        if (string.IsNullOrEmpty(senha)) campos["senha"] = "obrigatorio";
        else if (senha.Length < 8) campos["senha"] = "curto";
        else if (senha.Length > 120) campos["senha"] = "longo";

        if (aceitePrivacidade != true) campos["aceitePrivacidade"] = "valor_invalido";

        if (perfil == PerfisDoPainel.Corretor)
        {
            if (regioesNormalizadas.Count == 0) campos["regioes"] = "lista_vazia";
            else if (regioesNormalizadas.Any(regiao => regiao.Length > 120)) campos["regioes"] = "longo";

            if (especialidadesNormalizadas.Count == 0) campos["especialidades"] = "lista_vazia";
            else if (especialidadesNormalizadas.Any(especialidade =>
                especialidade is not Solar.Api.Dominio.Especialidades.Moradia and not Solar.Api.Dominio.Especialidades.Investimento))
            {
                campos["especialidades"] = "valor_invalido";
            }
        }

        return campos;
    }

    private static IReadOnlyList<string> NormalizarLista(IEnumerable<string> valores) =>
        valores.Where(valor => !string.IsNullOrWhiteSpace(valor))
            .Select(valor => valor.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool EhConflitoDeEmail(DbUpdateException erro) =>
        erro.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_corretores_email_normalizado",
        };
}
