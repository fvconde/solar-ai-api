namespace Solar.Api.Dominio;

public sealed class RegistroMetricas
{
    public int Id { get; private set; }

    public DateTimeOffset HistoricoDesde { get; private set; }
}
