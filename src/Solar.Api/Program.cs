using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Agendamentos;
using Solar.Api.Agente;
using Solar.Api.Conversas;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

const string PoliticaCorsFront = "front";
const string PoliticaRateLimitMensagens = "mensagens";
const string PoliticaRateLimitExclusao = "exclusao";
const string PoliticaRateLimitPainel = "painel";

var builder = WebApplication.CreateBuilder(args);

// API baseada em controllers (ASP.NET Core MVC), nao minimal API: o dominio do
// Solar cresce por area -- leads, conversas, agendamentos, metricas -- e um
// controller por area mantem rota, validacao e injecao de dependencia no mesmo
// lugar em vez de espalhados por delegates no Program.cs.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// Falhar aqui, e nao na primeira requisicao: sem string de conexao a API nao
// tem o que fazer, e um 500 no primeiro turno esconderia um erro de ambiente.
var conexaoPostgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres nao esta configurada.");

builder.Services.AddDbContext<SolarDbContext>(opcoes => opcoes.UseNpgsql(conexaoPostgres));

builder.Services.AddScoped<ConversaRepositorio>();
builder.Services.AddScoped<EncaminhamentoRepositorio>();
builder.Services.AddScoped<AgendaRepositorio>();
builder.Services.AddScoped<GravacaoDoTurno>();
builder.Services.AddSingleton<TravaDeConversas>();
builder.Services.AddHostedService<ServicoDeReengajamento>();

builder.Services.AddCors(opcoes => opcoes.AddPolicy(PoliticaCorsFront, politica => politica
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

        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limite,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    opcoes.AddPolicy(PoliticaRateLimitExclusao, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var limite = builder.Configuration.GetValue("RateLimiting:ExclusoesPorMinuto", 10);

        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limite,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
    opcoes.AddPolicy(PoliticaRateLimitPainel, httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo";
        var limite = builder.Configuration.GetValue("RateLimiting:PainelPorMinuto", 60);

        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limite,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    });
});

builder.Services.AddHttpClient<AgenteClient>((servicos, http) =>
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

app.UseCors(PoliticaCorsFront);
app.UseRateLimiter();

app.MapControllers();

app.Run();
