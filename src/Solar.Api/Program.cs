using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solar.Api.Agendamentos;
using Solar.Api.Agente;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

const string PoliticaCorsFront = "front";
const string PoliticaRateLimitMensagens = "mensagens";
const string PoliticaRateLimitExclusao = "exclusao";
const string PoliticaRateLimitPainel = "painel";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

const string chaveUrlBaseDoFront = "Painel:UrlBaseDoFront";
var urlBaseDoFront = builder.Configuration[chaveUrlBaseDoFront];
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

var conexaoPostgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres nao esta configurada.");

builder.Services.AddDbContext<SolarDbContext>(opcoes => opcoes.UseNpgsql(conexaoPostgres));
builder.Services.AddScoped<ConversaRepositorio>();
builder.Services.AddScoped<EncaminhamentoRepositorio>();
builder.Services.AddScoped<AgendaRepositorio>();
builder.Services.AddScoped<GravacaoDoTurno>();
builder.Services.AddSingleton<TravaDeConversas>();
builder.Services.AddHostedService<ServicoDeReengajamento>();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IPainelRateLimitStore, PainelRateLimitStore>();
builder.Services.AddScoped<IEnviadorEmail, EnviadorEmail>();
builder.Services.AddSingleton<IPasswordHasher<Corretor>>(_ => PasswordHasherDoCorretor.Criar());

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CorretorAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CorretorAuthenticationDefaults.AuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, CorretorAuthenticationHandler>(
        CorretorAuthenticationDefaults.AuthenticationScheme,
        _ => { });
builder.Services.AddAuthorization();

builder.Services.AddCors(opcoes => opcoes.AddPolicy(
    PoliticaCorsFront,
    politica => politica
        .WithOrigins(builder.Configuration.GetSection("Cors:Origens").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()));

builder.Services.AddRateLimiter(opcoes =>
{
    opcoes.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    opcoes.AddPolicy(PoliticaRateLimitMensagens, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var limite = builder.Configuration.GetValue("RateLimiting:MensagensPorMinuto", 30);
        return RateLimitPartition.GetFixedWindowLimiter(
            $"ip:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    opcoes.AddPolicy(PoliticaRateLimitExclusao, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var limite = builder.Configuration.GetValue("RateLimiting:ExclusoesPorMinuto", 10);
        return RateLimitPartition.GetFixedWindowLimiter(
            $"ip:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    opcoes.AddPolicy(PoliticaRateLimitPainel, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var caminho = httpContext.Request.Path.Value ?? string.Empty;
        var email = httpContext.Items.TryGetValue(
            EmailNormalizadoRateLimitMiddleware.ItemEmailNormalizado,
            out var item)
            ? item as string
            : null;

        var ehIdentificacao = caminho.Equals("/painel/identificacao", StringComparison.OrdinalIgnoreCase);
        var ehRecuperacao = caminho.Equals("/painel/senha/recuperacoes", StringComparison.OrdinalIgnoreCase);
        var limite = ehIdentificacao
            ? builder.Configuration.GetValue("RateLimiting:PainelIdentificacaoPorMinuto", 20)
            : ehRecuperacao
                ? builder.Configuration.GetValue("RateLimiting:PainelRecuperacaoPorEmailPorMinuto", 5)
                : builder.Configuration.GetValue("RateLimiting:PainelPorMinuto", 60);

        // O corpo ja foi lido e devolvido ao model binder pelo middleware
        // anterior. Recuperacao ganha particao por e-mail normalizado; as
        // demais chamadas, inclusive a identificacao, ficam particionadas por
        // IP para impedir varredura do cadastro.
        var particao = ehRecuperacao && email is not null
            ? $"email:{email}"
            : $"ip:{ip}";

        return RateLimitPartition.GetFixedWindowLimiter(
            particao,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limite,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });
});

builder.Services.AddHttpClient<AgenteClient>((servicos, http) =>
{
    var configuracao = servicos.GetRequiredService<IConfiguration>();
    http.BaseAddress = new Uri(configuracao["Agente:BaseUrl"] ?? "http://localhost:8000");
    http.Timeout = TimeSpan.FromSeconds(configuracao.GetValue("Agente:TimeoutSegundos", 30));
});

builder.Services.AddHttpClient<ResumoClient>((servicos, http) =>
{
    var configuracao = servicos.GetRequiredService<IConfiguration>();
    http.BaseAddress = new Uri(configuracao["Agente:BaseUrl"] ?? "http://localhost:8000");
    http.Timeout = TimeSpan.FromSeconds(configuracao.GetValue("Agente:TimeoutSegundos", 30));
});

var app = builder.Build();

await MigracaoDoBanco.AplicarAsync(app);
await AgendaInicial.GarantirAsync(app);

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Solar API v1"));

app.UseRouting();
app.UseCors(PoliticaCorsFront);
app.UseMiddleware<EmailNormalizadoRateLimitMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
