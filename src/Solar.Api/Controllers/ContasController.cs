using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Claims;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
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
    EncaminhamentoRepositorio encaminhamentos,
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

    [HttpGet("/api/conta")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<ContaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ContaResponse>> ObterAsync(CancellationToken cancellationToken)
    {
        var conta = await ContaAtualAsync(cancellationToken);
        return conta is null
            ? Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."))
            : Ok(await ProjecoesS44.ContaAsync(db, conta, cancellationToken));
    }

    [HttpPatch("/api/conta")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType<ContaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidacaoResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ContaResponse>> AtualizarAsync(
        [FromBody] AtualizarContaRequest? request,
        CancellationToken cancellationToken)
    {
        var conta = await ContaAtualAsync(cancellationToken);
        if (conta is null)
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }
        if (request is null)
        {
            return BadRequest(new ValidacaoResponse("validacao", new Dictionary<string, string> { ["corpo"] = "obrigatorio" }));
        }
        if (conta.Perfil == PerfisDoPainel.Cliente && (request.Regioes is not null || request.Especialidades is not null))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErroApiResponse("nao_permitido", "O perfil não permite alterar atuação de corretor."));
        }

        var mutavel = request.Nome is not null || request.Telefone is not null || request.Email is not null
            || request.Regioes is not null || request.Especialidades is not null;
        var campos = new Dictionary<string, string>(StringComparer.Ordinal);
        string? emailNormalizado = null;
        var telefoneNormalizado = request.Telefone is null ? null : Contato.Telefone(request.Telefone);
        var regioes = request.Regioes is null ? conta.Regioes : NormalizarLista(request.Regioes);
        var especialidades = request.Especialidades is null ? conta.Especialidades : NormalizarLista(request.Especialidades);

        if (!mutavel) campos["corpo"] = "obrigatorio";
        if (request.Nome is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Nome)) campos["nome"] = "obrigatorio";
            else if (request.Nome.Trim().Length < 2) campos["nome"] = "curto";
            else if (request.Nome.Trim().Length > 120) campos["nome"] = "longo";
        }
        if (request.Email is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Email)) campos["email"] = "obrigatorio";
            else if (!NormalizadorDeEmail.TentarNormalizar(request.Email, out emailNormalizado)) campos["email"] = "formato";
            if (string.IsNullOrWhiteSpace(request.SenhaAtual)) campos["senhaAtual"] = "obrigatorio";
        }
        if (request.Telefone is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Telefone)) campos["telefone"] = "obrigatorio";
            else if (telefoneNormalizado is null || telefoneNormalizado.Length is < 10 or > 11) campos["telefone"] = "formato";
        }
        if (request.Regioes is not null)
        {
            if (regioes.Count == 0) campos["regioes"] = "lista_vazia";
            else if (regioes.Any(regiao => regiao.Length > 120)) campos["regioes"] = "longo";
        }
        if (request.Especialidades is not null)
        {
            if (especialidades.Count == 0) campos["especialidades"] = "lista_vazia";
            else if (especialidades.Any(especialidade =>
                especialidade is not Solar.Api.Dominio.Especialidades.Moradia and not Solar.Api.Dominio.Especialidades.Investimento))
            {
                campos["especialidades"] = "valor_invalido";
            }
        }
        if (campos.Count > 0)
        {
            return BadRequest(new ValidacaoResponse("validacao", campos));
        }

        if (request.Email is not null)
        {
            if (conta.SenhaHash is null || passwordHasher.VerifyHashedPassword(conta, conta.SenhaHash, request.SenhaAtual!) == PasswordVerificationResult.Failed)
            {
                return BadRequest(new ErroApiResponse("senha_atual_incorreta", "A senha atual não confere."));
            }
            if (await db.Corretores.AnyAsync(c => c.EmailNormalizado == emailNormalizado && c.Id != conta.Id, cancellationToken))
            {
                return Conflict(new ErroApiResponse("email_em_uso", "O e-mail já está em uso."));
            }
        }

        if (request.Nome is not null) conta.AtualizarNome(request.Nome.Trim());
        if (request.Telefone is not null) conta.AtualizarTelefone(telefoneNormalizado!);
        if (request.Email is not null) conta.AtualizarEmail(request.Email.Trim(), emailNormalizado!);
        if (request.Regioes is not null || request.Especialidades is not null)
        {
            conta.AtualizarAtuacao(regioes, especialidades);
        }
        if (request.Email is not null && !await RevogarOutrasSessoesAsync(conta.Id, cancellationToken))
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException erro) when (EhConflitoDeEmail(erro))
        {
            return Conflict(new ErroApiResponse("email_em_uso", "O e-mail já está em uso."));
        }
        return Ok(await ProjecoesS44.ContaAsync(db, conta, cancellationToken));
    }

    [HttpPost("/api/conta/senha")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidacaoResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AlterarSenhaAsync(
        [FromBody] AlterarSenhaContaRequest? request,
        CancellationToken cancellationToken)
    {
        var conta = await ContaAtualAsync(cancellationToken);
        if (conta is null)
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }
        if (request is null || string.IsNullOrWhiteSpace(request.SenhaAtual) || string.IsNullOrEmpty(request.NovaSenha))
        {
            return BadRequest(new ValidacaoResponse("validacao", new Dictionary<string, string>
            {
                [request?.SenhaAtual is null or "" ? "senhaAtual" : "novaSenha"] = "obrigatorio",
            }));
        }
        if (request.NovaSenha.Length < 8 || request.NovaSenha.Length > 120)
        {
            return BadRequest(new ValidacaoResponse("validacao", new Dictionary<string, string>
            {
                ["novaSenha"] = request.NovaSenha.Length < 8 ? "curto" : "longo",
            }));
        }
        if (conta.SenhaHash is null || passwordHasher.VerifyHashedPassword(conta, conta.SenhaHash, request.SenhaAtual) == PasswordVerificationResult.Failed)
        {
            return BadRequest(new ErroApiResponse("senha_atual_incorreta", "A senha atual não confere."));
        }
        conta.DefinirSenhaHash(passwordHasher.HashPassword(conta, request.NovaSenha));
        if (!await RevogarOutrasSessoesAsync(conta.Id, cancellationToken))
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete("/api/conta")]
    [Authorize(AuthenticationSchemes = CorretorAuthenticationDefaults.AuthenticationScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErroApiResponse>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ExcluirContaAsync(
        [FromBody] ExcluirContaRequest? request,
        CancellationToken cancellationToken)
    {
        var conta = await ContaAtualAsync(cancellationToken);
        if (conta is null)
        {
            return Unauthorized(new ErroApiResponse("sessao_invalida", "A sessão não é válida."));
        }
        if (conta.Perfil == PerfisDoPainel.Supervisor)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new ErroApiResponse("nao_permitido", "Supervisores não podem excluir a própria conta."));
        }
        if (request is null || !NormalizadorDeEmail.TentarNormalizar(request.Email, out var emailConfirmacao)
            || emailConfirmacao != conta.EmailNormalizado)
        {
            return BadRequest(new ErroApiResponse("confirmacao_invalida", "A confirmação não confere."));
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        if (conta.Perfil == PerfisDoPainel.Cliente)
        {
            await ExcluirConversasDoClienteAsync(conta.Id, cancellationToken);
        }
        else if (conta.Perfil == PerfisDoPainel.Corretor)
        {
            await encaminhamentos.RedistribuirAsync(conta.Id, timeProvider.GetUtcNow(), cancellationToken);
        }
        db.Corretores.Remove(conta);
        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
        Response.Cookies.Delete(CorretorAuthenticationDefaults.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !environment.IsDevelopment(),
            Path = "/",
        });
        return NoContent();
    }

    private async Task<Corretor?> ContaAtualAsync(CancellationToken cancellationToken)
    {
        var id = ContaIdAutenticada();
        return id is null
            ? null
            : await db.Corretores.SingleOrDefaultAsync(c => c.Id == id && c.Ativo, cancellationToken);
    }

    private Guid? ContaIdAutenticada()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        var valor = User.FindFirstValue(CorretorAuthenticationDefaults.CorretorIdClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(valor, out var id) ? id : null;
    }

    private async Task<bool> RevogarOutrasSessoesAsync(Guid contaId, CancellationToken cancellationToken)
    {
        var valor = User.FindFirstValue(CorretorAuthenticationDefaults.SessaoIdClaim);
        if (!Guid.TryParse(valor, out var sessaoAtualId))
        {
            return false;
        }
        var sessoes = await db.Sessoes
            .Where(s => s.CorretorId == contaId && s.Id != sessaoAtualId && s.RevogadaEm == null)
            .ToListAsync(cancellationToken);
        var agora = timeProvider.GetUtcNow();
        foreach (var sessao in sessoes)
        {
            sessao.Revogar(agora);
        }
        return true;
    }

    private async Task ExcluirConversasDoClienteAsync(Guid contaId, CancellationToken cancellationToken)
    {
        var conversasDoCliente = await db.Conversas
            .Where(c => c.ContaId == contaId)
            .ToListAsync(cancellationToken);
        if (conversasDoCliente.Count == 0)
        {
            return;
        }

        var conversaIds = conversasDoCliente.Select(c => c.Id).ToArray();
        var leadIds = conversasDoCliente.Select(c => c.LeadId).Distinct().ToArray();
        var mensagens = await db.Mensagens.Where(m => conversaIds.Contains(m.ConversaId)).ToListAsync(cancellationToken);
        var encaminhamentosDoCliente = await db.Encaminhamentos
            .Where(e => conversaIds.Contains(e.ConversaId))
            .ToListAsync(cancellationToken);
        db.Mensagens.RemoveRange(mensagens);
        db.Encaminhamentos.RemoveRange(encaminhamentosDoCliente);
        db.Conversas.RemoveRange(conversasDoCliente);
        await db.SaveChangesAsync(cancellationToken);

        var leadsSemConversas = await db.Leads
            .Where(lead => leadIds.Contains(lead.Id) && !db.Conversas.Any(c => c.LeadId == lead.Id))
            .ToListAsync(cancellationToken);
        db.Leads.RemoveRange(leadsSemConversas);
        await db.SaveChangesAsync(cancellationToken);
    }

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
