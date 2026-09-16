using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Solar.Api.Contracts;

namespace Solar.Api.Tests;

public class ContratoResumoTeste
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Espelho_tem_os_mesmos_campos_e_obrigatoriedade_do_openapi_python()
    {
        Assert.Equal(
            ["perfilLead", "historico", "imoveis"],
            CamposJson<ResumoRequest>());
        Assert.Equal(
            ["perfil", "orcamento", "imoveis", "objecoes", "proximoPasso"],
            CamposJson<ResumoResponse>());

        Assert.All(Parametros<ResumoRequest>(), parametro => Assert.False(parametro.HasDefaultValue));
        Assert.All(Parametros<ResumoResponse>(), parametro => Assert.False(parametro.HasDefaultValue));

        var nulabilidade = new NullabilityInfoContext();
        Assert.All(
            typeof(ResumoResponse).GetProperties(),
            campo => Assert.Equal(NullabilityState.Nullable, nulabilidade.Create(campo).ReadState));
    }

    [Fact]
    public void Espelho_repete_os_limites_de_historico_e_imoveis()
    {
        var historico = Parametros<ResumoRequest>()
            .Single(parametro => parametro.Name == nameof(ResumoRequest.Historico));
        var imoveis = Parametros<ResumoRequest>()
            .Single(parametro => parametro.Name == nameof(ResumoRequest.Imoveis));

        Assert.Equal(1, historico.GetCustomAttribute<MinLengthAttribute>()?.Length);
        Assert.Equal(
            ContratoTurno.LimiteHistorico,
            historico.GetCustomAttribute<MaxLengthAttribute>()?.Length);
        Assert.Equal(
            ContratoTurno.LimiteImoveis,
            imoveis.GetCustomAttribute<MaxLengthAttribute>()?.Length);
    }

    [Theory]
    [InlineData(typeof(ResumoRequest), "{\"perfilLead\":{},\"historico\":[],\"imoveis\":[],\"canal\":\"web\"}")]
    [InlineData(typeof(ResumoResponse), "{\"perfil\":null,\"orcamento\":null,\"imoveis\":null,\"objecoes\":null,\"proximoPasso\":null,\"canal\":\"web\"}")]
    public void Campo_desconhecido_e_recusado(Type tipo, string corpo)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(corpo, tipo, Json));
    }

    [Fact]
    public void Historico_vazio_viola_o_contrato()
    {
        var request = new ResumoRequest(new PerfilLead(), [], []);
        var parametro = Parametros<ResumoRequest>()
            .Single(item => item.Name == nameof(ResumoRequest.Historico));
        var erros = new List<ValidationResult>();

        var valido = Validator.TryValidateValue(
            request.Historico,
            new ValidationContext(request) { MemberName = nameof(ResumoRequest.Historico) },
            erros,
            parametro.GetCustomAttributes<ValidationAttribute>());

        Assert.False(valido);
        Assert.Contains(erros, erro => erro.MemberNames.Contains(nameof(ResumoRequest.Historico)));
    }

    private static string[] CamposJson<T>() =>
        typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(campo => Json.PropertyNamingPolicy!.ConvertName(campo.Name))
            .ToArray();

    private static ParameterInfo[] Parametros<T>() =>
        typeof(T).GetConstructors().Single().GetParameters();
}
