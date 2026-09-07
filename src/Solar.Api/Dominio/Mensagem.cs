using Solar.Api.Contracts;

namespace Solar.Api.Dominio;

/// <summary>
/// Uma fala da conversa. Papel e <c>lead</c> ou <c>agente</c>.
/// </summary>
public sealed class Mensagem
{
    private Mensagem()
    {
    }

    /// <summary>
    /// Sequencial do banco, e nao Guid, de proposito: as duas mensagens de um
    /// turno sao gravadas com o mesmo <see cref="Em"/>, entao ordenar por
    /// carimbo de tempo empata. O identity desempata e da ordem estavel.
    /// </summary>
    public long Id { get; private set; }

    public Guid ConversaId { get; private set; }

    public string Papel { get; private set; } = Papeis.Lead;

    public string Texto { get; private set; } = string.Empty;

    public DateTimeOffset Em { get; private set; }

    public static Mensagem Nova(Guid conversaId, string papel, string texto, DateTimeOffset em) => new()
    {
        ConversaId = conversaId,
        Papel = papel,
        Texto = texto,
        Em = em,
    };

    public MensagemHistorico ParaContrato() => new(Papel, Texto, Em);
}
