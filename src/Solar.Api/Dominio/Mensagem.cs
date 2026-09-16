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

    /// <summary>
    /// O desfecho que o agente devolveu neste turno. Nulo nas falas do lead, e e
    /// o que permite reconstruir os eventos da trilha ao recarregar a pagina.
    /// Fora do <see cref="MensagemHistorico"/> de proposito: o agente nao precisa
    /// do desfecho de turnos passados, e o contrato do /turn e espelhado no
    /// Python.
    /// </summary>
    public string? ProximaAcao { get; private set; }

    public string? StatusAgendamento { get; private set; }

    public long? SlotId { get; private set; }

    public Slot? Slot { get; private set; }

    /// <summary>
    /// Snapshot dos imoveis sugeridos pela Lia neste turno.
    /// Invariante do card S-36: nulo nas falas do lead, lista (vazia ou com
    /// itens) nas falas da Lia. Guarda o snapshot completo incluindo o motivo
    /// contextual daquele turno, e nao apenas o ID.
    /// </summary>
    public List<ImovelSugerido>? ImoveisSugeridos { get; private set; }

    public static Mensagem DoLead(Guid conversaId, string texto, DateTimeOffset em) => new()
    {
        ConversaId = conversaId,
        Papel = Papeis.Lead,
        Texto = texto,
        Em = em,
        ImoveisSugeridos = null,
    };

    public static Mensagem DaLia(
        Guid conversaId,
        string texto,
        string proximaAcao,
        DateTimeOffset em,
        IReadOnlyList<ImovelSugerido>? imoveisSugeridos = null) => new()
    {
        ConversaId = conversaId,
        Papel = Papeis.Agente,
        Texto = texto,
        ProximaAcao = proximaAcao,
        Em = em,
        ImoveisSugeridos = imoveisSugeridos is null ? [] : [.. imoveisSugeridos],
    };

    public void RegistrarAgendamento(string status, long? slotId)
    {
        if (Papel != Papeis.Agente)
        {
            throw new InvalidOperationException("agendamento so pode pertencer a fala da Lia");
        }

        StatusAgendamento = status;
        SlotId = slotId;
    }

    public MensagemHistorico ParaContrato() => new(Papel, Texto, Em);
}
