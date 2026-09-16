using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public class ResumoPersistenciaTeste
{
    [Fact]
    public void Resumo_e_jsonb_anulavel_e_faz_round_trip_pelo_conversor()
    {
        var opcoes = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql("Host=localhost;Database=solar;Username=solar")
            .Options;
        using var db = new SolarDbContext(opcoes);
        var entidade = db.Model.FindEntityType(typeof(Encaminhamento))!;
        var propriedade = entidade.FindProperty(nameof(Encaminhamento.Resumo))!;
        var tabela = StoreObjectIdentifier.Table("encaminhamentos", schema: null);

        Assert.True(propriedade.IsNullable);
        Assert.Equal("jsonb", propriedade.GetColumnType());
        Assert.Equal("resumo", propriedade.GetColumnName(tabela));

        var esperado = new ResumoResponse(
            "Compra para moradia.",
            "Ate R$ 900 mil em Pinheiros.",
            null,
            "Nao quer terreo.",
            "Agendar visita.");
        var conversor = propriedade.GetValueConverter()!;
        var json = Assert.IsType<string>(conversor.ConvertToProvider(esperado));
        var restaurado = Assert.IsType<ResumoResponse>(conversor.ConvertFromProvider(json));

        Assert.Equal(esperado, restaurado);
    }
}
