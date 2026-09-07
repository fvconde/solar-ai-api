using Solar.Api.Contracts;

namespace Solar.Api.Dominio;

/// <summary>
/// A pessoa do outro lado da conversa e o que ja se sabe dela. Guarda o perfil
/// qualificado -- intencao, faixa de preco, quartos, regiao, urgencia, score.
/// </summary>
public sealed class Lead
{
    private Lead()
    {
    }

    /// <summary>
    /// UUIDv7 e ordenado no tempo: chaves geradas em sequencia caem juntas no
    /// indice B-tree, em vez de espalhar escrita por todas as paginas como um
    /// Guid v4 faz.
    /// </summary>
    public Guid Id { get; private set; }

    public string? Nome { get; private set; }

    public string? Intencao { get; private set; }

    public int? PrecoMin { get; private set; }

    public int? PrecoMax { get; private set; }

    public int? Quartos { get; private set; }

    public string? Regiao { get; private set; }

    public string? Urgencia { get; private set; }

    public string? ExpectativaRetorno { get; private set; }

    public int? Score { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }

    public DateTimeOffset AtualizadoEm { get; private set; }

    public static Lead Novo(DateTimeOffset em) => new()
    {
        Id = Guid.CreateVersion7(em),
        CriadoEm = em,
        AtualizadoEm = em,
    };

    /// <summary>
    /// Aplica o que o turno acrescentou. Campo nulo em <paramref name="extraidos"/>
    /// significa "nao mencionado agora" e nao apaga o que ja se sabia; intencao
    /// indefinida tambem nao apaga uma intencao ja conhecida.
    /// </summary>
    public void Fundir(string intencao, CamposExtraidos extraidos, DateTimeOffset em)
    {
        Nome = extraidos.Nome ?? Nome;
        Intencao = intencao is Intencoes.Indefinida or "" ? Intencao : intencao;
        PrecoMin = extraidos.PrecoMin ?? PrecoMin;
        PrecoMax = extraidos.PrecoMax ?? PrecoMax;
        Quartos = extraidos.Quartos ?? Quartos;
        Regiao = extraidos.Regiao ?? Regiao;
        Urgencia = extraidos.Urgencia ?? Urgencia;
        ExpectativaRetorno = extraidos.ExpectativaRetorno ?? ExpectativaRetorno;
        Score = extraidos.Score ?? Score;
        AtualizadoEm = em;
    }

    public PerfilLead ParaContrato() => new(
        Nome,
        Intencao,
        PrecoMin,
        PrecoMax,
        Quartos,
        Regiao,
        Urgencia,
        ExpectativaRetorno,
        Score);
}
