using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class AutenticacaoPainelPostgresTeste
{
    [Fact]
    public async Task Postgres_login_persiste_apenas_hash_da_sessao_e_configura_cookie_persistente()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var id = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@tests.solar.local";
        var hasher = CriarHasher();
        var corretor = CriarCorretor(id, "Corretor Postgres", email, "senha-de-teste", hasher);
        db.Corretores.Add(corretor);
        await db.SaveChangesAsync();

        var controller = CriarController(db, hasher, new EnviadorCaptura(), new RelogioFixo());
        var resultado = await controller.CriarSessaoAsync(
            new SessaoPainelRequest(email, "senha-de-teste"), default);

        var ok = Assert.IsType<OkObjectResult>(resultado.Result);
        Assert.IsType<SessaoPainelResponse>(ok.Value);

        var setCookie = controller.Response.Headers.SetCookie.ToString();
        var token = ExtrairCookie(setCookie);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Contains("solar_corretor_session=", setCookie);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=315360000", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);

        var sessao = await db.Sessoes.SingleAsync(s => s.CorretorId == id);
        Assert.Equal(32, sessao.TokenHash.Length);
        Assert.Equal(TokenSeguro.Sha256(token), sessao.TokenHash);
        Assert.NotEqual(token, Convert.ToHexString(sessao.TokenHash));
    }

    [Fact]
    public async Task Postgres_reset_consume_token_revoga_sessoes_anteriores_e_cria_nova_sessao()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var id = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@tests.solar.local";
        var hasher = CriarHasher();
        var remetente = new EnviadorCaptura();
        var relogio = new RelogioFixo();
        db.Corretores.Add(CriarCorretor(id, "Corretor Reset", email, "senha-antiga", hasher));
        await db.SaveChangesAsync();

        var controller = CriarController(db, hasher, remetente, relogio);
        var login = await controller.CriarSessaoAsync(
            new SessaoPainelRequest(email, "senha-antiga"), default);
        Assert.IsType<OkObjectResult>(login.Result);

        var pedido = await controller.SolicitarRecuperacaoAsync(
            new RecuperacaoSenhaRequest(email), default);
        Assert.IsType<AcceptedResult>(pedido);
        Assert.Equal(email, remetente.Destinatario);
        Assert.StartsWith(
            "http://localhost:4242/entrar?token=",
            remetente.Link,
            StringComparison.Ordinal);
        var token = ExtrairTokenDoLink(remetente.Link!);

        var valido = await controller.ValidarRecuperacaoAsync(token, default);
        var respostaValida = Assert.IsType<OkObjectResult>(valido.Result);
        Assert.Equal(new RecuperacaoSenhaTokenResponse(email), respostaValida.Value);

        var fraca = await controller.RedefinirSenhaAsync(
            new NovaSenhaPainelRequest(token, "curta"), default);
        var erroPolitica = Assert.IsType<BadRequestObjectResult>(fraca.Result);
        Assert.Equal(new ErroPainelResponse("politica"), erroPolitica.Value);

        var redefinida = await controller.RedefinirSenhaAsync(
            new NovaSenhaPainelRequest(token, "senha-nova-segura"), default);
        Assert.IsType<OkObjectResult>(redefinida.Result);

        var consumido = await db.RecuperacoesSenha.SingleAsync(r => r.CorretorId == id);
        Assert.NotNull(consumido.UsadaEm);
        Assert.Equal(32, consumido.TokenHash.Length);
        Assert.Equal(TokenSeguro.Sha256(token), consumido.TokenHash);

        var sessoes = await db.Sessoes
            .Where(s => s.CorretorId == id)
            .OrderBy(s => s.CriadaEm)
            .ToListAsync();
        Assert.Equal(2, sessoes.Count);
        Assert.NotNull(sessoes[0].RevogadaEm);
        Assert.Null(sessoes[1].RevogadaEm);

        var depoisDoUso = await controller.ValidarRecuperacaoAsync(token, default);
        var expirado = Assert.IsType<StatusCodeResult>(depoisDoUso.Result);
        Assert.Equal(StatusCodes.Status410Gone, expirado.StatusCode);
    }

    private static PainelController CriarController(
        SolarDbContext db,
        IPasswordHasher<Corretor> hasher,
        IEnviadorEmail enviador,
        TimeProvider relogio)
    {
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Loopback;
        contexto.Request.Scheme = "http";
        contexto.Request.Host = new HostString("localhost");

        var controller = new PainelController(
            db,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Painel:UrlBaseDoFront"] = "http://localhost:4242",
                    ["RateLimiting:PainelRecuperacaoPorIpPorMinuto"] = "10",
                    ["RateLimiting:PainelRecuperacaoPorEmailPorMinuto"] = "5",
                })
                .Build(),
            hasher,
            enviador,
            new PainelRateLimitStore(),
            relogio,
            new AmbienteDevelopment())
        {
            ControllerContext = new ControllerContext { HttpContext = contexto },
        };

        return controller;
    }

    private static Corretor CriarCorretor(
        Guid id,
        string nome,
        string email,
        string senha,
        IPasswordHasher<Corretor> hasher)
    {
        var corretor = (Corretor)Activator.CreateInstance(typeof(Corretor), nonPublic: true)!;
        typeof(Corretor).GetProperty(nameof(Corretor.Id))!.SetValue(corretor, id);
        typeof(Corretor).GetProperty(nameof(Corretor.Nome))!.SetValue(corretor, nome);
        typeof(Corretor).GetProperty(nameof(Corretor.Especialidade))!
            .SetValue(corretor, Especialidades.Moradia);
        typeof(Corretor).GetProperty(nameof(Corretor.ContatoInterno))!
            .SetValue(corretor, email);
        corretor.DefinirCredenciais(email, email.ToLowerInvariant(), hasher.HashPassword(corretor, senha));
        typeof(Corretor).GetProperty(nameof(Corretor.Regioes))!
            .SetValue(corretor, new List<string> { "sul" });
        typeof(Corretor).GetProperty(nameof(Corretor.Ativo))!.SetValue(corretor, true);
        typeof(Corretor).GetProperty(nameof(Corretor.CriadoEm))!
            .SetValue(corretor, new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        return corretor;
    }

    private static IPasswordHasher<Corretor> CriarHasher() => PasswordHasherDoCorretor.Criar();

    private static string ExtrairCookie(string setCookie)
    {
        const string prefixo = "solar_corretor_session=";
        var inicio = setCookie.IndexOf(prefixo, StringComparison.OrdinalIgnoreCase);
        Assert.True(inicio >= 0);
        inicio += prefixo.Length;
        var fim = setCookie.IndexOf(';', inicio);
        return setCookie[inicio..(fim < 0 ? setCookie.Length : fim)];
    }

    private static string ExtrairTokenDoLink(string link)
    {
        var query = new Uri(link).Query;
        const string prefixo = "?token=";
        Assert.StartsWith(prefixo, query, StringComparison.Ordinal);
        return Uri.UnescapeDataString(query[prefixo.Length..]);
    }

    private sealed class EnviadorCaptura : IEnviadorEmail
    {
        public string? Destinatario { get; private set; }
        public string? Link { get; private set; }

        public Task EnviarLinkRecuperacaoAsync(
            string destinatario,
            string link,
            CancellationToken cancellationToken = default)
        {
            Destinatario = destinatario;
            Link = link;
            return Task.CompletedTask;
        }
    }

    private sealed class RelogioFixo : TimeProvider
    {
        private readonly DateTimeOffset agora = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => agora;
    }

    private sealed class AmbienteDevelopment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = typeof(AutenticacaoPainelPostgresTeste).Assembly.GetName().Name!;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
