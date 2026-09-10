namespace Solar.Api.Dominio;

/// <summary>Horario de uma hora pertencente a agenda de um corretor.</summary>
public sealed class Slot
{
    private Slot()
    {
    }

    public long Id { get; private set; }

    public Guid CorretorId { get; private set; }

    public Corretor Corretor { get; private set; } = null!;

    public DateTimeOffset Inicio { get; private set; }

    public DateTimeOffset Fim { get; private set; }

    public Guid? LeadId { get; private set; }

    public Lead? Lead { get; private set; }

    public static Slot Novo(Guid corretorId, DateTimeOffset inicio, DateTimeOffset fim)
    {
        if (fim <= inicio)
        {
            throw new ArgumentException("o fim do slot precisa ser posterior ao inicio", nameof(fim));
        }

        return new Slot
        {
            CorretorId = corretorId,
            Inicio = inicio,
            Fim = fim,
        };
    }
}
