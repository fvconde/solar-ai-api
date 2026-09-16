using Solar.Api.Contracts;

namespace Solar.Api.Dominio;

public static class StatusDoLead
{
    public const string Novo = "novo";
    public const string Encaminhado = "encaminhado";
}

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

    /// <summary>
    /// Digitos, sem mascara. Nunca entra no <see cref="PerfilLead"/>: o contrato
    /// do turno nao tem campo para ele, e por isso nao ha caminho ate o modelo.
    /// </summary>
    public string? Telefone { get; private set; }

    public string? Email { get; private set; }

    public string Status { get; private set; } = StatusDoLead.Novo;

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

    public DateTimeOffset? ConsentimentoEm { get; private set; }

    public string? VersaoAvisoPrivacidade { get; private set; }

    public bool TemContato => Telefone is not null || Email is not null;

    public bool TemConsentimento => ConsentimentoEm is not null && VersaoAvisoPrivacidade is not null;

    public static Lead Novo(DateTimeOffset em) => new()
    {
        Id = Guid.CreateVersion7(em),
        Status = StatusDoLead.Novo,
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

    /// <summary>Grava o que o formulario do handoff coletou, ja normalizado.</summary>
    public void RegistrarContato(string? nome, string? telefone, string? email, DateTimeOffset em)
    {
        Nome = Contato.Nome(nome) ?? Nome;
        Telefone = Contato.Telefone(telefone) ?? Telefone;
        Email = Contato.Email(email) ?? Email;
        AtualizadoEm = em;
    }

    public void RegistrarConsentimento(string versaoAvisoPrivacidade, DateTimeOffset em)
    {
        if (VersaoAvisoPrivacidade == versaoAvisoPrivacidade && ConsentimentoEm is not null)
        {
            return;
        }

        ConsentimentoEm = em;
        VersaoAvisoPrivacidade = versaoAvisoPrivacidade;
        AtualizadoEm = em;
    }

    /// <summary>
    /// Traz para este lead o que a conversa recem-deduplicada ja sabia. Mesma
    /// regra do <see cref="Fundir"/>: valor presente e informacao nova e vence.
    /// </summary>
    public void Absorver(Lead outro, DateTimeOffset em)
    {
        Nome = outro.Nome ?? Nome;
        Intencao = outro.Intencao ?? Intencao;
        PrecoMin = outro.PrecoMin ?? PrecoMin;
        PrecoMax = outro.PrecoMax ?? PrecoMax;
        Quartos = outro.Quartos ?? Quartos;
        Regiao = outro.Regiao ?? Regiao;
        Urgencia = outro.Urgencia ?? Urgencia;
        ExpectativaRetorno = outro.ExpectativaRetorno ?? ExpectativaRetorno;
        Score = outro.Score ?? Score;

        if (outro.TemConsentimento &&
            (!TemConsentimento || outro.ConsentimentoEm!.Value > ConsentimentoEm!.Value))
        {
            ConsentimentoEm = outro.ConsentimentoEm;
            VersaoAvisoPrivacidade = outro.VersaoAvisoPrivacidade;
        }

        AtualizadoEm = em;
    }

    public void MarcarEncaminhado(DateTimeOffset em)
    {
        Status = StatusDoLead.Encaminhado;
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
