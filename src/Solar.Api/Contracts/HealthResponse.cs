using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

public static class HealthStatus
{
    public const string Up = "up";
    public const string Degraded = "degraded";
    public const string Down = "down";
}

public sealed record HealthCheckResult(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);

public sealed record HealthResponse(
    string Service,
    string Status,
    string Version,
    IReadOnlyDictionary<string, HealthCheckResult> Checks);

public static class HealthAggregation
{
    public static string Agregar(
        IReadOnlyDictionary<string, HealthCheckResult> checks,
        IReadOnlySet<string> essenciais)
    {
        var falhos = checks.Where(c => c.Value.Status != HealthStatus.Up).ToList();

        if (falhos.Count == 0)
        {
            return HealthStatus.Up;
        }

        return falhos.Any(c => essenciais.Contains(c.Key))
            ? HealthStatus.Down
            : HealthStatus.Degraded;
    }
}
