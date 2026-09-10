using Solar.Api.Dominio;

namespace Solar.Api.Tests;

public class ContatoTeste
{
    [Theory]
    [InlineData("(11) 99999-8888", "11999998888")]
    [InlineData("11 99999 8888", "11999998888")]
    [InlineData("+55 (11) 99999-8888", "5511999998888")]
    public void Telefone_vira_so_digitos(string bruto, string esperado) =>
        Assert.Equal(esperado, Contato.Telefone(bruto));

    [Fact]
    public void Mesmo_telefone_escrito_de_dois_jeitos_normaliza_igual() =>
        Assert.Equal(Contato.Telefone("(11) 99999-8888"), Contato.Telefone("11999998888"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem numero nenhum")]
    public void Telefone_sem_digito_vira_nulo(string? bruto) => Assert.Null(Contato.Telefone(bruto));

    [Fact]
    public void Email_normaliza_caixa_e_espaco() =>
        Assert.Equal("lia@solar.local", Contato.Email("  LIA@Solar.Local  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Email_em_branco_vira_nulo(string? bruto) => Assert.Null(Contato.Email(bruto));

    [Fact]
    public void Nome_perde_o_espaco_das_pontas() => Assert.Equal("Ana", Contato.Nome("  Ana  "));
}
