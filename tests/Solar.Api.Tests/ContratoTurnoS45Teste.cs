using System.Reflection;
using System.Text.Json;
using Solar.Api.Contracts;

namespace Solar.Api.Tests;

public class ContratoTurnoS45Teste
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Serializacao_inclui_essenciais_completos_em_camel_case_e_omite_snake_case(bool essenciaisCompletos)
    {
        var turno = new TurnoResponse(
            "Ola",
            Intencoes.Compra,
            new CamposExtraidos(),
            ProximasAcoes.ContinuarConversa,
            [],
            null,
            essenciaisCompletos);

        var json = JsonSerializer.Serialize(turno, Json);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("essenciaisCompletos", out var elemento));
        Assert.Equal(essenciaisCompletos, elemento.GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("essenciais_completos", out _));
        Assert.Contains(essenciaisCompletos ? "\"essenciaisCompletos\":true" : "\"essenciaisCompletos\":false", json);
        Assert.DoesNotContain("essenciais_completos", json);

        var deserializado = JsonSerializer.Deserialize<TurnoResponse>(json, Json);
        Assert.NotNull(deserializado);
        Assert.Equal(essenciaisCompletos, deserializado.EssenciaisCompletos);
    }

    [Theory]
    [InlineData("{\"resposta\":\"Ola\",\"intencao\":\"compra\",\"camposExtraidos\":{},\"proximaAcao\":\"continuar_conversa\",\"imoveisSugeridos\":[],\"slotEscolhido\":null,\"essenciaisCompletos\":true}", true)]
    [InlineData("{\"resposta\":\"Ola\",\"intencao\":\"compra\",\"camposExtraidos\":{},\"proximaAcao\":\"continuar_conversa\",\"imoveisSugeridos\":[],\"slotEscolhido\":null,\"essenciaisCompletos\":false}", false)]
    public void Deserializacao_le_essenciais_completos(string json, bool esperado)
    {
        var turno = JsonSerializer.Deserialize<TurnoResponse>(json, Json);

        Assert.NotNull(turno);
        Assert.Equal(esperado, turno.EssenciaisCompletos);
    }

    [Fact]
    public void Nome_camel_case_exato_no_contrato()
    {
        var propriedade = typeof(TurnoResponse).GetProperty(nameof(TurnoResponse.EssenciaisCompletos));
        Assert.NotNull(propriedade);

        var nomeJson = Json.PropertyNamingPolicy!.ConvertName(propriedade.Name);
        Assert.Equal("essenciaisCompletos", nomeJson);
    }

    [Fact]
    public void Deserializacao_recusa_snake_case()
    {
        var json = "{\"resposta\":\"Ola\",\"intencao\":\"compra\",\"camposExtraidos\":{},\"proximaAcao\":\"continuar_conversa\",\"imoveisSugeridos\":[],\"slotEscolhido\":null,\"essenciais_completos\":true}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TurnoResponse>(json, Json));
    }

    [Fact]
    public void Deserializacao_recusa_campo_desconhecido()
    {
        var json = "{\"resposta\":\"Ola\",\"intencao\":\"compra\",\"camposExtraidos\":{},\"proximaAcao\":\"continuar_conversa\",\"imoveisSugeridos\":[],\"slotEscolhido\":null,\"essenciaisCompletos\":true,\"campoDesconhecido\":\"valor\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TurnoResponse>(json, Json));
    }

    [Fact]
    public void Construtor_preserva_default_false_para_compatibilidade()
    {
        var turno = new TurnoResponse(
            "Ola",
            Intencoes.Compra,
            new CamposExtraidos(),
            ProximasAcoes.ContinuarConversa,
            [],
            null);

        Assert.False(turno.EssenciaisCompletos);
    }
}
