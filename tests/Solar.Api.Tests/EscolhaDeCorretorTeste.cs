using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;

namespace Solar.Api.Tests;

public class EscolhaDeCorretorTeste
{
    private static readonly DateTimeOffset Nascimento = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static CorretorCandidato Candidato(
        int ordem,
        string especialidade,
        string[] regioes,
        bool ativo = true,
        int carga = 0,
        DateTimeOffset? ultimo = null) =>
        new(
            Guid.Parse($"3f6b9c21-4d0a-4c7e-9a11-{ordem:D12}"),
            especialidade,
            regioes,
            ativo,
            carga,
            ultimo,
            Nascimento.AddMinutes(ordem));

    private static Guid Id(int ordem) => Guid.Parse($"3f6b9c21-4d0a-4c7e-9a11-{ordem:D12}");

    [Theory]
    [InlineData(Intencoes.Investimento, Especialidades.Investimento)]
    [InlineData(Intencoes.Compra, Especialidades.Moradia)]
    [InlineData(Intencoes.Aluguel, Especialidades.Moradia)]
    [InlineData(Intencoes.Indefinida, Especialidades.Moradia)]
    [InlineData(null, Especialidades.Moradia)]
    public void Trilha_define_a_especialidade(string? intencao, string esperada) =>
        Assert.Equal(esperada, EscolhaDeCorretor.EspecialidadeDe(intencao));

    [Fact]
    public void Investimento_nao_cai_em_corretor_de_moradia()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Moradia, ["sul"]),
            Candidato(2, Especialidades.Investimento, ["sul"]),
        };

        Assert.Equal(Id(2), EscolhaDeCorretor.Escolher(Intencoes.Investimento, "zona sul", candidatos));
    }

    [Fact]
    public void Compra_nao_cai_em_corretor_de_investimento()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Investimento, ["sul"]),
            Candidato(2, Especialidades.Moradia, ["sul"]),
        };

        Assert.Equal(Id(2), EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos));
    }

    [Fact]
    public void Regiao_fora_da_cobertura_nao_e_elegivel()
    {
        var candidatos = new[] { Candidato(1, Especialidades.Moradia, ["sul", "centro"]) };

        Assert.Null(EscolhaDeCorretor.Escolher(Intencoes.Compra, "Curitiba", candidatos));
    }

    [Fact]
    public void Regiao_ausente_nao_exclui_ninguem()
    {
        var candidatos = new[] { Candidato(1, Especialidades.Moradia, ["sul"]) };

        Assert.Equal(Id(1), EscolhaDeCorretor.Escolher(Intencoes.Compra, null, candidatos));
        Assert.Equal(Id(1), EscolhaDeCorretor.Escolher(Intencoes.Compra, "   ", candidatos));
    }

    [Theory]
    [InlineData("zona sul", true)]
    [InlineData("quero algo na Zona Sul mesmo", true)]
    [InlineData("consolacao", false)]
    [InlineData("insulado", false)]
    public void Termo_de_uma_palavra_casa_como_palavra_inteira(string regiao, bool esperado) =>
        Assert.Equal(esperado, EscolhaDeCorretor.Cobre(["sul"], regiao));

    [Fact]
    public void Termo_com_espaco_casa_como_trecho() =>
        Assert.True(EscolhaDeCorretor.Cobre(["vila mariana"], "procuro na Vila Mariana"));

    [Fact]
    public void Acento_do_lead_nao_impede_o_casamento() =>
        Assert.True(EscolhaDeCorretor.Cobre(["butanta"], "moro no Butanta"));

    [Fact]
    public void Menor_carga_aberta_vence()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Moradia, ["sul"], carga: 3),
            Candidato(2, Especialidades.Moradia, ["sul"], carga: 1),
            Candidato(3, Especialidades.Moradia, ["sul"], carga: 2),
        };

        Assert.Equal(Id(2), EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos));
    }

    [Fact]
    public void Empate_de_carga_desempata_pelo_mais_antigo_sem_lead()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Moradia, ["sul"], carga: 2, ultimo: Nascimento.AddDays(5)),
            Candidato(2, Especialidades.Moradia, ["sul"], carga: 2, ultimo: Nascimento.AddDays(1)),
        };

        Assert.Equal(Id(2), EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos));
    }

    [Fact]
    public void Quem_nunca_recebeu_lead_vem_antes_de_quem_ja_recebeu()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Moradia, ["sul"], carga: 0, ultimo: Nascimento.AddDays(1)),
            Candidato(2, Especialidades.Moradia, ["sul"], carga: 0, ultimo: null),
        };

        Assert.Equal(Id(2), EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos));
    }

    [Fact]
    public void Corretor_inativo_nao_e_elegivel()
    {
        var candidatos = new[] { Candidato(1, Especialidades.Moradia, ["sul"], ativo: false) };

        Assert.Null(EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos));
    }

    [Fact]
    public void Sem_nenhum_candidato_devolve_nulo() =>
        Assert.Null(EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", []));

    [Fact]
    public void Mesma_entrada_devolve_sempre_o_mesmo_corretor()
    {
        var candidatos = new[]
        {
            Candidato(1, Especialidades.Moradia, ["sul"]),
            Candidato(2, Especialidades.Moradia, ["sul"]),
            Candidato(3, Especialidades.Moradia, ["sul"]),
        };

        var escolhas = Enumerable
            .Range(0, 20)
            .Select(_ => EscolhaDeCorretor.Escolher(Intencoes.Compra, "zona sul", candidatos))
            .Distinct();

        Assert.Single(escolhas);
    }
}
