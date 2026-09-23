using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class S44MigracaoPostgresTeste
{
    [Fact]
    public async Task Migracoes_de_contas_aplicam_em_schema_postgres_vazio()
    {
        var connectionString = PostgresTestDatabase.ObterConexaoParaAplicacao();
        var schema = $"s44_vazio_{Guid.NewGuid():N}";
        await using var administrador = new NpgsqlConnection(connectionString);
        await administrador.OpenAsync();
        await using (var criarSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", administrador))
        {
            await criarSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var conexaoDoSchema = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema,
            };
            var options = new DbContextOptionsBuilder<SolarDbContext>()
                .UseNpgsql(conexaoDoSchema.ConnectionString)
                .Options;

            await using var db = new SolarDbContext(options);
            await db.Database.MigrateAsync();

            var migrations = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.Contains("20260923023342_S44ContasESessoes", migrations);
            Assert.Contains("20260923030409_S44DedupePorConta", migrations);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await using var removerSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", administrador);
            await removerSchema.ExecuteNonQueryAsync();
        }
    }
}
