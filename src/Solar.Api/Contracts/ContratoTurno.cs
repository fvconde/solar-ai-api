using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

public static class Papeis
{
    public const string Lead = "lead";
    public const string Agente = "agente";
}

public static class Intencoes
{
    public const string Compra = "compra";
    public const string Aluguel = "aluguel";
    public const string Investimento = "investimento";
    public const string Indefinida = "indefinida";
}

public static class Urgencias
{
    public const string Alta = "alta";
    public const string Media = "media";
    public const string Baixa = "baixa";
}

public static class ProximasAcoes
{
    public const string ContinuarConversa = "continuar_conversa";
    public const string SugerirImoveis = "sugerir_imoveis";
    public const string AgendarReuniao = "agendar_reuniao";
    public const string DirecionarEspecialista = "direcionar_especialista";
    public const string Encerrar = "encerrar";
}

public static class ContratoContato
{
    public const int LimiteNome = 200;
    public const int LimiteTelefone = 20;
    public const int LimiteEmail = 200;
}

public static class ContratoTurno
{
    public const int LimiteMensagem = 4000;
    public const int LimiteHistorico = 50;
    public const int LimiteImoveis = 5;
    public const int LimiteExpectativa = 200;
}

/// <summary>Uma mensagem ja trocada na conversa. Papel: lead ou agente.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MensagemHistorico(
    string Papel,
    string Texto,
    DateTimeOffset Em);

/// <summary>Perfil acumulado do lead, do qual a API e dona.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PerfilLead(
    string? Nome = null,
    string? Intencao = null,
    int? PrecoMin = null,
    int? PrecoMax = null,
    int? Quartos = null,
    string? Regiao = null,
    string? Urgencia = null,
    [StringLength(ContratoTurno.LimiteExpectativa)] string? ExpectativaRetorno = null,
    [Range(0, 100)] int? Score = null);

/// <summary>O que este turno acrescentou ao perfil. Campo nulo = nao mencionado.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CamposExtraidos(
    string? Nome = null,
    int? PrecoMin = null,
    int? PrecoMax = null,
    int? Quartos = null,
    string? Regiao = null,
    string? Urgencia = null,
    [StringLength(ContratoTurno.LimiteExpectativa)] string? ExpectativaRetorno = null,
    [Range(0, 100)] int? Score = null);

/// <summary>Imovel recomendado no turno, com o que a UI precisa para desenhar o card.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImovelSugerido(
    string Id,
    string Tipo,
    string Bairro,
    int Quartos,
    int Metragem,
    int? PrecoVenda,
    int? PrecoAluguel,
    string Motivo);

/// <summary>Horario livre do corretor atribuido que a Lia pode oferecer.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SlotOferecido(
    long Id,
    DateTimeOffset Inicio,
    DateTimeOffset Fim);

/// <summary>Um turno de conversa entrando no agente.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TurnoRequest(
    Guid ConversaId,
    [StringLength(ContratoTurno.LimiteMensagem, MinimumLength = 1)]
    string Mensagem,
    [MaxLength(ContratoTurno.LimiteHistorico)]
    IReadOnlyList<MensagemHistorico> Historico,
    PerfilLead PerfilLead,
    IReadOnlyList<SlotOferecido> Agenda);

/// <summary>Um turno de conversa saindo do agente.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TurnoResponse(
    string Resposta,
    string Intencao,
    CamposExtraidos CamposExtraidos,
    string ProximaAcao,
    IReadOnlyList<ImovelSugerido> ImoveisSugeridos,
    long? SlotEscolhido);
