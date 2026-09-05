using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Solar.Api.Controllers;

/// <summary>
/// Prova de vida do servico. O /health toca o Postgres de proposito: e o que
/// transforma "o container subiu" em prova de que a rede do compose funciona.
/// </summary>
[ApiController]
public class HealthController(IConfiguration configuration, ILogger<HealthController> logger) : ControllerBase
{
    [HttpGet("/")]
    public IActionResult Raiz() => Ok(new { service = "solar-ai-api", status = "up" });

    [HttpGet("/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var db = "down";

        try
        {
            // Conexao crua por enquanto. O acesso a dados de verdade (EF Core,
            // repositorios, migrations) entra no S-06 -- aqui so precisa provar
            // que a API alcanca o banco pelo DNS interno do compose.
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("Postgres"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            db = "up";
        }
        catch (Exception ex)
        {
            // Nunca logar a connection string: ela carrega a senha do banco.
            logger.LogError(ex, "Falha ao conectar no Postgres");
        }

        var payload = new { service = "solar-ai-api", status = db == "up" ? "up" : "degraded", db };

        return db == "up"
            ? Ok(payload)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
    }
}
