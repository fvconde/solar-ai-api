using Solar.Api.Agente;
using Solar.Api.Conversas;

var builder = WebApplication.CreateBuilder(args);

// API baseada em controllers (ASP.NET Core MVC), nao minimal API: o dominio do
// Solar cresce por area -- leads, conversas, agendamentos, metricas -- e um
// controller por area mantem rota, validacao e injecao de dependencia no mesmo
// lugar em vez de espalhados por delegates no Program.cs.
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ConversaStore>();

builder.Services.AddHttpClient<AgenteClient>((servicos, http) =>
{
    var configuracao = servicos.GetRequiredService<IConfiguration>();

    http.BaseAddress = new Uri(configuracao["Agente:BaseUrl"] ?? "http://localhost:8000");
    http.Timeout = TimeSpan.FromSeconds(configuracao.GetValue("Agente:TimeoutSegundos", 30));
});

var app = builder.Build();

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Solar API v1"));

app.MapControllers();

app.Run();
