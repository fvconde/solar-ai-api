using Microsoft.EntityFrameworkCore;

namespace Solar.Api.Persistencia;

public static class MigracaoDoBanco
{
    private const int Tentativas = 5;

    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Aplica as migrations pendentes no boot. E o que faz <c>docker compose up</c>
    /// e o deploy do S-26 subirem com o schema certo sem ninguem rodar
    /// <c>dotnet ef database update</c> a mao.
    ///
    /// <para>
    /// O compose ja espera o <c>pg_isready</c> antes de subir a API, mas em cloud
    /// nao ha healthcheck entre os dois: por isso as tentativas. Falhou nas cinco,
    /// a aplicacao nao sobe -- API de pe com schema velho e pior que API fora.
    /// </para>
    /// </summary>
    public static async Task AplicarAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(MigracaoDoBanco));

        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await using var escopo = app.Services.CreateAsyncScope();

                var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
                var pendentes = (await db.Database.GetPendingMigrationsAsync()).ToArray();

                if (pendentes.Length > 0)
                {
                    logger.LogInformation("Aplicando {Total} migration(s): {Migrations}",
                        pendentes.Length, string.Join(", ", pendentes));

                    await db.Database.MigrateAsync();
                }

                logger.LogInformation("Banco em dia.");

                return;
            }
            catch (Exception erro) when (tentativa < Tentativas)
            {
                // Nunca logar a excecao inteira aqui: a mensagem do Npgsql pode
                // carregar a connection string, e ela tem a senha do banco.
                logger.LogWarning("Banco indisponivel na tentativa {Tentativa}/{Total}: {Tipo}. Nova tentativa em {Espera}s.",
                    tentativa, Tentativas, erro.GetType().Name, Espera.TotalSeconds);

                await Task.Delay(Espera);
            }
        }
    }
}
