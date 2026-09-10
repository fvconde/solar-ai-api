using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

internal static class PostgresTestDatabase
{
    internal const string CollectionName = "Postgres";
    private const string NomeBancoEsperado = "solar_test";

    internal static async Task<SolarDbContext> CriarContextoAsync()
    {
        string conexao;
        try
        {
            conexao = ObterConexao();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Falha na resolucao da connection string de testes: {ex.Message}");
            throw;
        }

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;

        var db = new SolarDbContext(options);

        try
        {
            if (!await db.Database.CanConnectAsync())
            {
                Assert.Fail(
                    $"Nao foi possivel conectar ao banco de testes dedicado '{NomeBancoEsperado}'. " +
                    "A validacao obrigatoria de integracao precisa executar e passar.");
            }

            await db.Database.MigrateAsync();
            return db;
        }
        catch (Exception ex)
        {
            await db.DisposeAsync();
            Assert.Fail($"Falha ao conectar ao banco de testes dedicado '{NomeBancoEsperado}': {ex.Message}");
            throw;
        }
    }

    private static string ObterConexao()
    {
        var configurada = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresTest");
        if (!string.IsNullOrWhiteSpace(configurada))
        {
            ValidarBancoDedicado(configurada);
            return configurada;
        }

        var envPath = LocalizarArquivoEnv();
        if (envPath is not null)
        {
            var linhas = File.ReadAllLines(envPath);
            var usuario = linhas.FirstOrDefault(linha => linha.StartsWith("POSTGRES_USER="))
                ?.Split('=', 2)[1].Trim() ?? "solar";
            var senha = linhas.FirstOrDefault(linha => linha.StartsWith("POSTGRES_PASSWORD="))
                ?.Split('=', 2)[1].Trim();

            if (!string.IsNullOrWhiteSpace(senha))
            {
                var montada =
                    $"Host=localhost;Port=5432;Database={NomeBancoEsperado};Username={usuario};Password={senha}";
                ValidarBancoDedicado(montada);
                return montada;
            }
        }

        throw new InvalidOperationException(
            "Configuracao para o banco de testes dedicado nao encontrada. " +
            "Defina ConnectionStrings__PostgresTest ou certifique-se de que o arquivo .env contem POSTGRES_PASSWORD.");
    }

    private static void ValidarBancoDedicado(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Database, NomeBancoEsperado, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SEGURANCA: testes de integracao exigem o banco descartavel '{NomeBancoEsperado}', " +
                $"mas a conexao aponta para '{builder.Database}'.");
        }
    }

    private static string? LocalizarArquivoEnv()
    {
        var diretorio = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            if (string.IsNullOrEmpty(diretorio))
            {
                break;
            }

            var local = Path.Combine(diretorio, ".env");
            if (File.Exists(local))
            {
                return local;
            }

            var repo = Path.Combine(diretorio, "solar-ai-api", ".env");
            if (File.Exists(repo))
            {
                return repo;
            }

            diretorio = Directory.GetParent(diretorio)?.FullName;
        }

        return null;
    }
}

[CollectionDefinition(PostgresTestDatabase.CollectionName, DisableParallelization = true)]
public sealed class PostgresTestCollection;
