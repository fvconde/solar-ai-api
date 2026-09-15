using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

public class PainelTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    private static SolarDbContext CriarBanco()
    {
        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new SolarDbContext(options);
    }

    private static IConfiguration CriarConfiguracao() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Painel:UrlBaseDoFront"] = "http://localhost:4242",
                ["RateLimiting:PainelRecuperacaoPorIpPorMinuto"] = "10",
                ["RateLimiting:PainelRecuperacaoPorEmailPorMinuto"] = "5",
            })
            .Build();

    private static PainelController CriarController(
        SolarDbContext db,
        Guid? corretorId = null,
        IEnviadorEmail? enviadorEmail = null)
    {
        var contexto = new DefaultHttpContext();

        if (corretorId is not null)
        {
            var id = corretorId.Value.ToString("D");
            contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(CorretorAuthenticationDefaults.CorretorIdClaim, id),
                    new Claim(ClaimTypes.NameIdentifier, id),
                },
                CorretorAuthenticationDefaults.AuthenticationScheme));
        }

        var controller = new PainelController(
            db,
            CriarConfiguracao(),
            PasswordHasherDoCorretor.Criar(),
            enviadorEmail ?? new EnviadorEmailDeTeste(),
            new PainelRateLimitStore(),
            new RelogioDeTeste(),
            new AmbienteDeTeste())
        {
            ControllerContext = new ControllerContext { HttpContext = contexto },
        };
        return controller;
    }

    private static Corretor CriarCorretor(
        Guid id,
        string nome,
        string email,
        string? senha = null,
        bool ativo = true)
    {
        var corretor = (Corretor)Activator.CreateInstance(typeof(Corretor), nonPublic: true)!;
        typeof(Corretor).GetProperty(nameof(Corretor.Id))!.SetValue(corretor, id);
        typeof(Corretor).GetProperty(nameof(Corretor.Nome))!.SetValue(corretor, nome);
        typeof(Corretor).GetProperty(nameof(Corretor.Especialidade))!
            .SetValue(corretor, Especialidades.Moradia);
        typeof(Corretor).GetProperty(nameof(Corretor.ContatoInterno))!
            .SetValue(corretor, $"{nome.ToLowerInvariant()}@solar.local");
        typeof(Corretor).GetProperty(nameof(Corretor.Email))!.SetValue(corretor, email);
        typeof(Corretor).GetProperty(nameof(Corretor.EmailNormalizado))!
            .SetValue(corretor, email.Trim().ToLowerInvariant());
        typeof(Corretor).GetProperty(nameof(Corretor.Regioes))!
            .SetValue(corretor, new List<string> { "sul", "oeste" });
        typeof(Corretor).GetProperty(nameof(Corretor.Ativo))!.SetValue(corretor, ativo);
        typeof(Corretor).GetProperty(nameof(Corretor.CriadoEm))!.SetValue(corretor, Agora);

        if (senha is not null)
        {
            var hasher = CriarHasher();
            corretor.DefinirSenhaHash(hasher.HashPassword(corretor, senha));
        }

        return corretor;
    }

    private static IPasswordHasher<Corretor> CriarHasher() => PasswordHasherDoCorretor.Criar();

    [Fact]
    public async Task Identificacao_diferencia_cadastrado_e_formato()
    {
        using var db = CriarBanco();
        var corretor = CriarCorretor(Guid.NewGuid(), "Helena Braga", "Helena@Solar.com");
        db.Corretores.Add(corretor);
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var cadastrado = await controller.IdentificarAsync(
            new IdentificacaoPainelRequest(" helena@solar.com "), default);
        var resposta = Assert.IsType<OkObjectResult>(cadastrado.Result);
        Assert.Equal(new IdentificacaoPainelResponse(true), resposta.Value);

        var formato = await controller.IdentificarAsync(
            new IdentificacaoPainelRequest("nao-e-email"), default);
        var erro = Assert.IsType<BadRequestObjectResult>(formato.Result);
        Assert.Equal(new ErroPainelResponse("formato"), erro.Value);
    }

    [Fact]
    public async Task Recuperacao_monta_link_para_tela_do_front_com_token_escapado()
    {
        using var db = CriarBanco();
        var email = "helena@solar.com";
        db.Corretores.Add(CriarCorretor(Guid.NewGuid(), "Helena Braga", email));
        await db.SaveChangesAsync();

        var remetente = new EnviadorEmailDeTeste();
        var controller = CriarController(db, enviadorEmail: remetente);
        controller.HttpContext.Request.Scheme = "https";
        controller.HttpContext.Request.Host = new HostString("api.example.test");

        var resultado = await controller.SolicitarRecuperacaoAsync(
            new RecuperacaoSenhaRequest(email),
            default);

        Assert.IsType<AcceptedResult>(resultado);
        Assert.NotNull(remetente.Link);
        var link = remetente.Link!;
        const string prefixo = "http://localhost:4242/entrar?token=";
        Assert.StartsWith(prefixo, link, StringComparison.Ordinal);
        Assert.DoesNotContain("api.example.test", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/painel/senha/recuperacoes/", link, StringComparison.Ordinal);

        var tokenEscapado = link[prefixo.Length..];
        Assert.NotEmpty(tokenEscapado);
        Assert.Equal(
            Uri.EscapeDataString(Uri.UnescapeDataString(tokenEscapado)),
            tokenEscapado);
    }

    [Fact]
    public async Task Login_sucesso_usa_claim_no_leads_e_preserva_filtros()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga", "helena@solar.com", "senha-segura"));

        var lead = Lead.Novo(Agora);
        lead.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 100", Score: 100), Agora);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var login = await controller.CriarSessaoAsync(
            new SessaoPainelRequest("HELENA@SOLAR.COM", "senha-segura"), default);
        Assert.IsType<OkObjectResult>(login.Result);

        // O controller de fila usa a identidade da sessao, nao um header externo.
        var fila = await CriarController(db, corretorId)
            .ListarLeadsAsync(Intencoes.Compra, meusLeads: null, default);
        var ok = Assert.IsType<OkObjectResult>(fila.Result);
        var resposta = Assert.IsType<FilaLeadsResponse>(ok.Value);

        Assert.Single(resposta.Leads);
        Assert.Equal("Lead 100", resposta.Leads[0].Nome);
    }

    [Fact]
    public async Task Cinco_falhas_bloqueiam_por_trinta_segundos_e_acerto_nao_contorna_bloqueio()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga", "helena@solar.com", "senha-segura"));
        await db.SaveChangesAsync();
        var controller = CriarController(db);

        for (var tentativa = 1; tentativa <= 4; tentativa++)
        {
            var falha = await controller.CriarSessaoAsync(
                new SessaoPainelRequest("helena@solar.com", "errada"), default);
            var erro = Assert.IsType<UnauthorizedObjectResult>(falha.Result);
            Assert.Equal(5 - tentativa, ((TentativasRestantesResponse)erro.Value!).TentativasRestantes);
        }

        var bloqueio = await controller.CriarSessaoAsync(
            new SessaoPainelRequest("helena@solar.com", "errada"), default);
        var bloqueado = Assert.IsType<ObjectResult>(bloqueio.Result);
        Assert.Equal(StatusCodes.Status423Locked, bloqueado.StatusCode);
        Assert.Equal(30, ((BloqueadoPorSegundosResponse)bloqueado.Value!).BloqueadoPorSegundos);

        var durante = await controller.CriarSessaoAsync(
            new SessaoPainelRequest("helena@solar.com", "senha-segura"), default);
        var aindaBloqueado = Assert.IsType<ObjectResult>(durante.Result);
        Assert.Equal(StatusCodes.Status423Locked, aindaBloqueado.StatusCode);
    }

    [Fact]
    public async Task Recuperacao_em_InMemory_localiza_hash_por_comparacao_de_bytes_e_consumo_unico()
    {
        using var db = CriarBanco();
        var id = Guid.NewGuid();
        var email = "helena@solar.com";
        var corretor = CriarCorretor(id, "Helena Braga", email, "senha-antiga");
        var token = TokenSeguro.Criar();
        var criadoEm = Agora;
        db.Corretores.Add(corretor);
        db.RecuperacoesSenha.Add(RecuperacaoSenha.Nova(
            id,
            TokenSeguro.Sha256(token),
            criadoEm,
            criadoEm.AddMinutes(30)));
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var valido = await controller.ValidarRecuperacaoAsync(token, default);
        Assert.IsType<OkObjectResult>(valido.Result);

        var redefinida = await controller.RedefinirSenhaAsync(
            new NovaSenhaPainelRequest(token, "senha-nova"), default);
        Assert.IsType<OkObjectResult>(redefinida.Result);

        var segundaLeitura = await controller.ValidarRecuperacaoAsync(token, default);
        var expirado = Assert.IsType<StatusCodeResult>(segundaLeitura.Result);
        Assert.Equal(StatusCodes.Status410Gone, expirado.StatusCode);
    }

    [Fact]
    public async Task Fila_sem_claim_devolve_401_e_nao_aceita_X_Corretor_Id()
    {
        using var db = CriarBanco();
        var controller = CriarController(db);
        controller.HttpContext.Request.Headers["X-Corretor-Id"] = Guid.NewGuid().ToString();

        var resultado = await controller.ListarLeadsAsync(null, null, default);

        Assert.IsType<UnauthorizedResult>(resultado.Result);
    }

    [Fact]
    public async Task Handler_da_sessao_converte_cookie_opaco_em_claim()
    {
        using var db = CriarBanco();
        var id = Guid.NewGuid();
        var corretor = CriarCorretor(id, "Helena Braga", "helena@solar.com", "senha-segura");
        var token = TokenSeguro.Criar();
        db.Corretores.Add(corretor);
        db.Sessoes.Add(SessaoCorretor.Nova(id, TokenSeguro.Sha256(token), Agora));
        await db.SaveChangesAsync();

        var colecaoServicos = new ServiceCollection();
        colecaoServicos.AddLogging();
        colecaoServicos.AddSingleton(db);
        colecaoServicos.AddAuthentication(CorretorAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, CorretorAuthenticationHandler>(
                CorretorAuthenticationDefaults.AuthenticationScheme,
                _ => { });
        using var servicos = colecaoServicos.BuildServiceProvider();

        var contexto = new DefaultHttpContext { RequestServices = servicos };
        contexto.Request.Headers.Cookie =
            $"{CorretorAuthenticationDefaults.CookieName}={token}";
        var autenticacao = servicos.GetRequiredService<IAuthenticationService>();

        var resultado = await autenticacao.AuthenticateAsync(
            contexto,
            CorretorAuthenticationDefaults.AuthenticationScheme);

        Assert.True(resultado.Succeeded);
        Assert.Equal(
            id.ToString("D"),
            resultado.Principal!.FindFirstValue(CorretorAuthenticationDefaults.CorretorIdClaim));
    }

    [Fact]
    public async Task Fila_preserva_ordenacao_score_e_filtro_meus_leads()
    {
        using var db = CriarBanco();
        var corretorAId = Guid.NewGuid();
        var corretorBId = Guid.NewGuid();
        db.Corretores.AddRange(
            CriarCorretor(corretorAId, "Helena Braga", "helena@solar.com"),
            CriarCorretor(corretorBId, "Rafael Nunes", "rafael@solar.com"));

        var leadA = Lead.Novo(Agora.AddMinutes(-2));
        leadA.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead A", Score: 45), Agora.AddMinutes(-2));
        var leadB = Lead.Novo(Agora.AddMinutes(-1));
        leadB.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead B", Score: 100), Agora.AddMinutes(-1));
        db.Leads.AddRange(leadA, leadB);
        db.Encaminhamentos.Add(Encaminhamento.Novo(
            Guid.NewGuid(), leadA.Id, corretorAId, Especialidades.Moradia, Agora));
        await db.SaveChangesAsync();

        var controller = CriarController(db, corretorAId);
        var geral = await controller.ListarLeadsAsync(null, false, default);
        var filaGeral = Assert.IsType<FilaLeadsResponse>(Assert.IsType<OkObjectResult>(geral.Result).Value);
        Assert.Equal(new[] { "Lead B", "Lead A" }, filaGeral.Leads.Select(l => l.Nome));

        var meus = await controller.ListarLeadsAsync(null, true, default);
        var filaMeus = Assert.IsType<FilaLeadsResponse>(Assert.IsType<OkObjectResult>(meus.Result).Value);
        Assert.Single(filaMeus.Leads);
        Assert.Equal("Lead A", filaMeus.Leads[0].Nome);
    }

    private sealed class EnviadorEmailDeTeste : IEnviadorEmail
    {
        public string? Link { get; private set; }

        public Task EnviarLinkRecuperacaoAsync(
            string destinatario,
            string link,
            CancellationToken cancellationToken = default)
        {
            Link = link;
            return Task.CompletedTask;
        }
    }

    private sealed class RelogioDeTeste : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private sealed class AmbienteDeTeste : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = typeof(PainelTeste).Assembly.GetName().Name!;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
