namespace Solar.Api.Dominio;

public static class Especialidades
{
    public const string Moradia = "moradia";
    public const string Investimento = "investimento";
}

/// <summary>
/// O corretor humano que assume o lead depois do encaminhamento. A base e
/// semeada por migration: nao ha tela de gestao.
/// </summary>
public sealed class Corretor
{
    private Corretor()
    {
    }

    public Guid Id { get; private set; }

    public string Nome { get; private set; } = string.Empty;

    public string Especialidade { get; private set; } = Especialidades.Moradia;

    /// <summary>Termos de cobertura -- zonas ou bairros -- casados contra a regiao do lead.</summary>
    public List<string> Regioes { get; private set; } = [];

    public string ContatoInterno { get; private set; } = string.Empty;

    public bool Ativo { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }
}
