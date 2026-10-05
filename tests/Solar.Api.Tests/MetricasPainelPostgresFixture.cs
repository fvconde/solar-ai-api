using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Solar.Api.Conversas;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public sealed class MetricasPainelPostgresFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset Agora = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset HistoricoDesde = Agora.AddDays(-400);
    private readonly string schema = "s22_metricas_" + Guid.NewGuid().ToString("N");
    private string conexao = string.Empty;
    private readonly string? conexaoAplicacaoAnterior =
        Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    public MetricasPainelApiFactory Factory { get; private set; } = null!;
    public RelogioMetricasPainel Relogio { get; } = new(Agora);
    public int PrazoInicial { get; private set; }

    public async Task InitializeAsync()
    {
        conexao = PostgresTestDatabase.ObterConexaoParaAplicacao();
        await using var admin = new NpgsqlConnection(conexao);
        await admin.OpenAsync();
        await using var criar = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
        await criar.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(conexao) { SearchPath = schema };
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", builder.ConnectionString);
        Factory = new MetricasPainelApiFactory(builder.ConnectionString, schema, Relogio);
        using var client = Factory.CreateClient();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
            await db.RegistroMetricas.Where(r => r.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoricoDesde, HistoricoDesde));
        }
        PrazoInicial = Factory.Services.GetRequiredService<IConfiguration>()
            .GetValue<int>("Expurgo:PrazoRetencaoMeses");
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (Factory is not null)
            {
                await Factory.DisposeAsync();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", conexaoAplicacaoAnterior);
            await using var admin = new NpgsqlConnection(conexao);
            await admin.OpenAsync();
            await using var remover = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin);
            await remover.ExecuteNonQueryAsync();
        }
    }
}

public sealed class MetricasPainelApiFactory(
    string conexao, string schema, RelogioMetricasPainel relogio) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<SolarDbContext>();
            services.AddScoped(_ => new SolarDbContext(
                new DbContextOptionsBuilder<SolarDbContext>()
                    .UseNpgsql(conexao, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                    .Options));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(relogio);
            foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                (s.ImplementationType == typeof(ServicoDeExpurgo) ||
                 s.ImplementationType == typeof(ServicoDeReengajamento))).ToArray())
            {
                services.Remove(service);
            }
        });
    }
}

public sealed class RelogioMetricasPainel(DateTimeOffset agora) : TimeProvider
{
    private int chamadas;
    public int Chamadas => chamadas;
    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref chamadas);
        return agora;
    }
}
