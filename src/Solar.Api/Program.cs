var builder = WebApplication.CreateBuilder(args);

// API baseada em controllers (ASP.NET Core MVC), nao minimal API: o dominio do
// Solar cresce por area -- leads, conversas, agendamentos, metricas -- e um
// controller por area mantem rota, validacao e injecao de dependencia no mesmo
// lugar em vez de espalhados por delegates no Program.cs.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Solar API v1"));

app.MapControllers();

app.Run();
