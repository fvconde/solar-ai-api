using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Solar.Api.Contracts;

namespace Solar.Api.Controllers;

/// <summary>
/// Prova de vida do servico. O /health toca o Postgres de proposito: e o que
/// transforma "o container subiu" em prova de que a rede do compose funciona.
/// </summary>
[ApiController]
[Produces("application/json")]
public class HealthController(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<HealthController> logger) : ControllerBase
{
    private const string Servico = "solar-ai-api";

    private static readonly IReadOnlySet<string> Essenciais = new HashSet<string> { "postgres" };

    [HttpGet("/")]
    public IActionResult Raiz() => Ok(new { service = Servico });

    [HttpGet("/health")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken cancellationToken)
    {
        var checks = new Dictionary<string, HealthCheckResult>
        {
            ["postgres"] = await ChecarPostgresAsync(cancellationToken),
        };

        var status = HealthAggregation.Agregar(checks, Essenciais);
        var payload = new HealthResponse(Servico, status, Versao(), checks);

        return status == HealthStatus.Down
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, payload)
            : Ok(payload);
    }

    private async Task<HealthCheckResult> ChecarPostgresAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Conexao crua por enquanto. O acesso a dados de verdade (EF Core,
            // repositorios, migrations) entra no S-06 -- aqui so precisa provar
            // que a API alcanca o banco pelo DNS interno do compose.
            await using var connection = new NpgsqlConnection(configuration.GetConnectionString("Postgres"));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return new HealthCheckResult(HealthStatus.Up);
        }
        catch (Exception ex)
        {
            // Nunca logar a connection string: ela carrega a senha do banco.
            logger.LogError(ex, "Health check do Postgres falhou");

            return new HealthCheckResult(HealthStatus.Down, MotivoVisivel(ex));
        }
    }

    private string? MotivoVisivel(Exception ex)
    {
        if (!environment.IsDevelopment())
        {
            return null;
        }

        var primeiraLinha = ex.Message.Split('\n')[0].Trim();

        return primeiraLinha.Length <= 200 ? primeiraLinha : primeiraLinha[..200];
    }

    private string Versao() =>
        configuration["SOLAR_VERSION"] is { Length: > 0 } versao ? versao : "dev";
}
