using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed partial class ExclusaoTitularPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    private const string ChaveAdminSintetica = "privacidade-sintetica-t2";

    [Theory]
    [InlineData("sem_cookie")]
    [InlineData("errada")]
    [InlineData("cookie_de_A")]
    [InlineData("legada")]
    [InlineData("header_admin")]
    [InlineData("longa")]
    public async Task Prova_invalida_nao_apaga_conversa_lead_mensagens_ou_encaminhamentos(string caso)
    {
        using var app = CriarAplicacao();
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = caso == "legada" ? await CriarLegadaAsync() : await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var antesA = await FotografarAsync(a.Id);
        var antesB = await FotografarAsync(b.Id);
        if (caso == "errada") AdicionarCookie(cliente, CookieChave(TokenSeguro.Criar()));
        if (caso is "cookie_de_A" or "legada") AdicionarCookie(cliente, a.Cookie!);
        if (caso == "longa") AdicionarCookie(cliente, CookieChave(new string('x', 513)));
        if (caso == "header_admin") cliente.DefaultRequestHeaders.Add("X-Chave-Privacidade", ChaveAdminSintetica);

        using var resposta = await cliente.DeleteAsync($"/conversas/{b.Id:D}/titular");

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        AssertSemRemocaoCookie(resposta);
        Assert.Equal(antesA, await FotografarAsync(a.Id));
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    public async Task Anonima_unica_apaga_todo_lead_e_limpa_cookie_com_mesmos_atributos(string ambiente, bool secure)
    {
        using var app = CriarAplicacao(ambiente: ambiente);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var antesB = await FotografarAsync(b.Id);
        AdicionarCookie(cliente, a.Cookie!);

        using var resposta = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");

        await AssertEscopoAsync(resposta, "lead_e_vinculos", true, b);
        AssertCookieRemovido(resposta, a.Id, secure);
        await AssertConversaAusenteAsync(a.Id);
        await AssertLeadAusenteAsync(a.LeadId);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversa_com_conta_exige_login_dono_mesmo_quando_cookie_anonimo_ainda_e_valido(bool donoEnviaChave)
    {
        using var app = CriarAplicacao();
        using var anonimo = CriarCliente(app);
        var conversa = await CriarAnonimaAsync(anonimo);
        var dona = await CriarContaAsync(app);
        var outra = await CriarContaAsync(app);
        await using (var db = await PostgresTestDatabase.CriarContextoAsync())
            await new ConversaRepositorio(db).VincularContaAsync(conversa.Id, dona.Id, DateTimeOffset.UtcNow, default);
        await SemearRegistrosAsync(conversa.Id);
        var antes = await FotografarAsync(conversa.Id);
        AdicionarCookie(anonimo, conversa.Cookie!);
        using var recusadaAnonima = await anonimo.DeleteAsync($"/conversas/{conversa.Id:D}/titular");
        Assert.Equal(HttpStatusCode.Forbidden, recusadaAnonima.StatusCode);
        AssertSemRemocaoCookie(recusadaAnonima);
        Assert.Equal(antes, await FotografarAsync(conversa.Id));

        using var clienteOutra = await EntrarAsync(app, outra);
        AdicionarCookie(clienteOutra, conversa.Cookie!);
        using var recusadaOutra = await clienteOutra.DeleteAsync($"/conversas/{conversa.Id:D}/titular");
        Assert.Equal(HttpStatusCode.Forbidden, recusadaOutra.StatusCode);
        AssertSemRemocaoCookie(recusadaOutra);
        Assert.Equal(antes, await FotografarAsync(conversa.Id));

        using var clienteDona = await EntrarAsync(app, dona);
        if (donoEnviaChave) AdicionarCookie(clienteDona, conversa.Cookie!);
        using var autorizada = await clienteDona.DeleteAsync($"/conversas/{conversa.Id:D}/titular");
        await AssertEscopoAsync(autorizada, "lead_e_vinculos", true);
        AssertCookieRemovido(autorizada, conversa.Id, false);
        await AssertConversaAusenteAsync(conversa.Id);
        await AssertLeadAusenteAsync(conversa.LeadId);
        await using var verificar = await PostgresTestDatabase.CriarContextoAsync();
        Assert.True(await verificar.Corretores.AnyAsync(c => c.Id == dona.Id));
        Assert.True(await verificar.Sessoes.AnyAsync(s => s.CorretorId == dona.Id));
    }

    [Fact]
    public async Task Conversas_do_mesmo_lead_e_da_mesma_conta_autenticada_autorizam_cascata()
    {
        using var app = CriarAplicacao();
        var dona = await CriarContaAsync(app);
        using var cliente = await EntrarAsync(app, dona);
        var a = await CriarComContaAsync(cliente);
        var b = await CriarComContaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var telefone = TelefoneUnico();
        var lead = await RegistrarContatoAsync(cliente, a.Id, telefone, null);
        Assert.Equal(lead, await RegistrarContatoAsync(cliente, b.Id, telefone, null));

        using var resposta = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");

        await AssertEscopoAsync(resposta, "lead_e_vinculos", true, b);
        await AssertConversaAusenteAsync(a.Id);
        await AssertConversaAusenteAsync(b.Id);
        await AssertLeadAusenteAsync(lead);
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        Assert.True(await db.Corretores.AnyAsync(c => c.Id == dona.Id));
        Assert.True(await db.Sessoes.AnyAsync(s => s.CorretorId == dona.Id));
    }

    [Theory]
    [InlineData("telefone")]
    [InlineData("email")]
    public async Task Dedupe_real_preserva_conversa_sem_prova_e_limita_exclusao_a_conversa_provada(string contato)
    {
        using var app = CriarAplicacao();
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var telefone = contato == "telefone" ? TelefoneUnico() : null;
        var email = contato == "email" ? $"dedupe-{Guid.NewGuid():N}@tests.solar.local" : null;
        var lead = await RegistrarContatoAsync(cliente, b.Id, telefone, email);
        Assert.Equal(lead, await RegistrarContatoAsync(cliente, a.Id, telefone, email));
        var antesB = await FotografarAsync(b.Id);
        AdicionarCookie(cliente, a.Cookie!);

        using var resposta = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");

        var body = await AssertEscopoAsync(resposta, "apenas_conversa", false, b);
        Assert.Contains("canal humano", body);
        Assert.False(email is not null && body.Contains(email, StringComparison.Ordinal));
        Assert.False(telefone is not null && body.Contains(telefone, StringComparison.Ordinal));
        AssertCookieRemovido(resposta, a.Id, false);
        await AssertConversaAusenteAsync(a.Id);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        Assert.True(await db.Leads.AnyAsync(l => l.Id == lead));
        Assert.True(TokenSeguro.HashesIguais(
            (await db.Conversas.SingleAsync(c => c.Id == b.Id)).ChaveExclusaoHash,
            TokenSeguro.Sha256(b.Chave!)));
    }

    [Fact]
    public async Task Canal_administrativo_preserva_sua_autorizacao_e_sua_cascata()
    {
        using var app = CriarAplicacao();
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        var telefone = TelefoneUnico();
        var lead = await RegistrarContatoAsync(cliente, a.Id, telefone, null);
        Assert.Equal(lead, await RegistrarContatoAsync(cliente, b.Id, telefone, null));
        await SemearRegistrosAsync(a.Id, b.Id);
        AdicionarCookie(cliente, a.Cookie!);
        using var recusada = await cliente.DeleteAsync($"/conversas/{a.Id:D}?excluirLead=true");
        Assert.Equal(HttpStatusCode.Unauthorized, recusada.StatusCode);
        cliente.DefaultRequestHeaders.Add("X-Chave-Privacidade", ChaveAdminSintetica);

        using var resposta = await cliente.DeleteAsync($"/conversas/{a.Id:D}?excluirLead=true");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var resultado = await resposta.Content.ReadFromJsonAsync<ExclusaoConversaResponse>();
        Assert.NotNull(resultado);
        Assert.True(resultado.LeadExcluido);
        Assert.Equal("lead_e_vinculos", resultado.Escopo);
        await AssertConversaAusenteAsync(a.Id);
        await AssertConversaAusenteAsync(b.Id);
        await AssertLeadAusenteAsync(lead);
    }

    [Fact]
    public async Task Canal_titular_aplica_rate_limit_exclusao_sem_apagar_nem_limpar_cookie_na_recusa()
    {
        using var app = CriarAplicacao(limiteExclusoes: 1);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id);
        var antes = await FotografarAsync(a.Id);
        using var semCookie = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
        Assert.Equal(HttpStatusCode.Forbidden, semCookie.StatusCode);
        AdicionarCookie(cliente, a.Cookie!);

        using var limitada = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");

        Assert.Equal(HttpStatusCode.TooManyRequests, limitada.StatusCode);
        AssertSemRemocaoCookie(limitada);
        Assert.Equal(antes, await FotografarAsync(a.Id));
    }

    private WebApplicationFactory<Program> CriarAplicacao(
        DbCommandInterceptor? interceptor = null, string ambiente = "Development", int limiteExclusoes = 100) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(ambiente);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguranca:ChavePrivacidade"] = ChaveAdminSintetica,
                    ["RateLimiting:ExclusoesPorMinuto"] = limiteExclusoes.ToString(),
                }));
            if (interceptor is not null)
                builder.ConfigureTestServices(services => services.AddDbContext<SolarDbContext>(
                    options => options.AddInterceptors(interceptor)));
        });

    private static HttpClient CriarCliente(WebApplicationFactory<Program> app) => app.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private sealed record ConversaCriada(Guid Id, Guid LeadId, string? Cookie = null, string? Chave = null);
    private sealed record ContaCriada(Guid Id, string Email, string Senha);

    private static async Task<ConversaCriada> CriarAnonimaAsync(HttpClient cliente)
    {
        using var nascimento = await cliente.PostAsJsonAsync($"/conversas/{Guid.NewGuid():D}/consentimento",
            new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, nascimento.StatusCode);
        var dto = (await nascimento.Content.ReadFromJsonAsync<ConsentimentoResponse>())!;
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(nascimento.Headers.GetValues("Set-Cookie")));
        Assert.Equal(ConversasController.NomeCookieChaveExclusao, cookie.Name.ToString());
        return new(dto.ConversaId, dto.LeadId, CookieChave(cookie.Value.ToString()), cookie.Value.ToString());
    }

    private static async Task<ConversaCriada> CriarComContaAsync(HttpClient cliente)
    {
        var id = Guid.NewGuid();
        using var nascimento = await cliente.PostAsJsonAsync($"/conversas/{id:D}/consentimento",
            new { versaoAvisoPrivacidade = AvisoPrivacidade.VersaoAtual });
        Assert.Equal(HttpStatusCode.OK, nascimento.StatusCode);
        var dto = (await nascimento.Content.ReadFromJsonAsync<ConsentimentoResponse>())!;
        return new(id, dto.LeadId);
    }

    private static async Task<ConversaCriada> CriarLegadaAsync()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, DateTimeOffset.UtcNow);
        conversa.Lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();
        return new(conversa.Id, conversa.LeadId);
    }

    private static async Task SemearRegistrosAsync(params Guid[] ids)
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        foreach (var id in ids)
        {
            var conversa = await db.Conversas.Include(c => c.Lead).SingleAsync(c => c.Id == id);
            conversa.RegistrarTurno("Mensagem sintetica T2", new TurnoResponse(
                "Resposta sintetica T2", Intencoes.Compra, new CamposExtraidos(),
                ProximasAcoes.ContinuarConversa, [], null), DateTimeOffset.UtcNow);
            db.Encaminhamentos.Add(Encaminhamento.Novo(id, conversa.LeadId, null,
                Especialidades.Moradia, DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync();
    }

    private static async Task<ContaCriada> CriarContaAsync(WebApplicationFactory<Program> app)
    {
        const string senha = "Senha-sintetica-38";
        var email = $"titular-{Guid.NewGuid():N}@tests.solar.local";
        await using var escopo = app.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta("Cliente sintetico T2", email, email, "11999997777", "placeholder",
            PerfisDoPainel.Cliente, [], [], AvisoPrivacidade.VersaoAtual, DateTimeOffset.UtcNow);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return new(conta.Id, email, senha);
    }

    private static async Task<HttpClient> EntrarAsync(WebApplicationFactory<Program> app, ContaCriada conta)
    {
        var cliente = CriarCliente(app);
        using var resposta = await cliente.PostAsJsonAsync("/api/sessoes", new { email = conta.Email, senha = conta.Senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        AdicionarCookie(cliente, resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
        return cliente;
    }

    private static async Task<Guid> RegistrarContatoAsync(HttpClient cliente, Guid id, string? telefone, string? email)
    {
        using var resposta = await cliente.PostAsJsonAsync($"/conversas/{id:D}/contato",
            new { nome = "Contato sintetico T2", telefone, email });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<ContatoResponse>())!.LeadId;
    }

    private static string TelefoneUnico() =>
        $"119{Convert.ToUInt32(Guid.NewGuid().ToString("N")[..8], 16) % 100_000_000:D8}";

    private static string CookieChave(string chave) => $"{ConversasController.NomeCookieChaveExclusao}={chave}";

    private static void AdicionarCookie(HttpClient cliente, string cookie)
    {
        var anteriores = cliente.DefaultRequestHeaders.TryGetValues("Cookie", out var valores)
            ? string.Join("; ", valores) + "; " : "";
        cliente.DefaultRequestHeaders.Remove("Cookie");
        cliente.DefaultRequestHeaders.Add("Cookie", anteriores + cookie);
    }

    private static async Task<string> FotografarAsync(Guid id)
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conversa = await db.Conversas.AsNoTracking().Where(c => c.Id == id).Select(c => new
        {
            c.Id, c.LeadId, c.ContaId, c.Canal, c.CriadaEm, c.AtualizadaEm, c.Desfecho, c.TentativasReengajamento,
        }).SingleAsync();
        var lead = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == conversa.LeadId);
        var mensagens = await db.Mensagens.AsNoTracking().Where(m => m.ConversaId == id).OrderBy(m => m.Id).ToListAsync();
        var encaminhamentos = await db.Encaminhamentos.AsNoTracking().Where(e => e.ConversaId == id).OrderBy(e => e.Id).ToListAsync();
        return JsonSerializer.Serialize(new { conversa, lead, mensagens, encaminhamentos });
    }

    private static async Task AssertConversaAusenteAsync(Guid id)
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        Assert.False(await db.Conversas.AnyAsync(c => c.Id == id));
        Assert.False(await db.Mensagens.AnyAsync(m => m.ConversaId == id));
        Assert.False(await db.Encaminhamentos.AnyAsync(e => e.ConversaId == id));
    }

    private static async Task AssertLeadAusenteAsync(Guid id)
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        Assert.False(await db.Leads.AnyAsync(l => l.Id == id));
        Assert.False(await db.Conversas.AnyAsync(c => c.LeadId == id));
        Assert.False(await db.Encaminhamentos.AnyAsync(e => e.LeadId == id));
    }

    private static async Task<string> AssertEscopoAsync(
        HttpResponseMessage resposta, string escopo, bool leadExcluido, ConversaCriada? outra = null)
    {
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var dto = await resposta.Content.ReadFromJsonAsync<ExclusaoTitularResponse>();
        Assert.NotNull(dto);
        Assert.Equal(escopo, dto.Escopo);
        Assert.Equal(leadExcluido, dto.LeadExcluido);
        var body = await resposta.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "leadExcluido", "removidoEm", "escopo", "mensagem" },
            json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        if (outra is not null)
        {
            Assert.False(body.Contains(outra.Id.ToString("D"), StringComparison.Ordinal));
            Assert.False(body.Contains(outra.LeadId.ToString("D"), StringComparison.Ordinal));
            Assert.False(outra.Chave is not null && body.Contains(outra.Chave, StringComparison.Ordinal));
        }
        return body;
    }

    private static void AssertSemRemocaoCookie(HttpResponseMessage resposta)
    {
        if (!resposta.Headers.TryGetValues("Set-Cookie", out var valores)) return;
        Assert.DoesNotContain(ConversasController.NomeCookieChaveExclusao,
            valores.Select(valor => SetCookieHeaderValue.Parse(valor).Name.ToString()));
    }

    private static void AssertCookieRemovido(HttpResponseMessage resposta, Guid id, bool secure)
    {
        var cookie = Assert.Single(resposta.Headers.GetValues("Set-Cookie").Select(valor => SetCookieHeaderValue.Parse(valor)),
            c => c.Name.Equals(ConversasController.NomeCookieChaveExclusao, StringComparison.Ordinal));
        Assert.True(cookie.HttpOnly);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.Equal(secure, cookie.Secure);
        Assert.Equal($"/conversas/{id:D}", cookie.Path.ToString());
        Assert.True(string.IsNullOrEmpty(cookie.Value.ToString()));
        Assert.True(cookie.Expires < DateTimeOffset.UtcNow);
        Assert.False(cookie.Domain.HasValue);
    }
}
