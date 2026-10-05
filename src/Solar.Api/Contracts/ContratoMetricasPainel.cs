using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

public sealed record MetricasPainelResponse(
    PeriodoMetricasPainel Periodo,
    int ConversasIniciadas,
    int HorariosConfirmados,
    int ReservasProximos7Dias,
    IntencoesMetricasPainel LeadsPorIntencao,
    EquipeMetricasPainel? Equipe,
    ExtrasMetricasPainel Extras,
    IReadOnlyList<AvancoMetricasPainel> Avanco,
    int DadosEssenciaisPreenchidos);

public sealed record AvancoMetricasPainel(
    string Etapa,
    int Conversas,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SemEssenciais = null);

public sealed record PeriodoMetricasPainel(int Dias, DateTimeOffset Inicio, DateTimeOffset AtualizadoEm, DateTimeOffset HistoricoDesde);

public sealed record IntencoesMetricasPainel(int Compra, int Aluguel, int Investimento, int SemIntencao);

public sealed record EquipeMetricasPainel(
    IReadOnlyList<AtribuicaoMetricasPainel> AtribuidasPorCorretor,
    int AguardandoCorretor,
    int PendentesAprovacao);

public sealed record AtribuicaoMetricasPainel(CorretorPainelResumo Corretor, int Conversas);

public sealed record ExtrasMetricasPainel(
    ScoreMetricasPainel Score,
    RegioesMetricasPainel Regioes,
    IReadOnlyList<ImovelMetricasPainel> Imoveis,
    IReadOnlyList<HorarioMetricasPainel> ProximosHorarios,
    PrivacidadeMetricasPainel Privacidade,
    double? TempoMedianoMin,
    IReadOnlyList<double?> TempoMedianoDiario,
    FollowUpMetricasPainel FollowUp);

public sealed record FollowUpMetricasPainel(
    int JanelaDias,
    int ComFollowUp,
    int JanelaEncerrada,
    int Responderam,
    int EmObservacao);

public sealed record ScoreMetricasPainel(int Frio, int Morno, int Quente, int SemAvaliacao);

public sealed record RegioesMetricasPainel(
    IReadOnlyList<RegiaoMetricasPainel> Top,
    int Outras,
    int Informaram,
    int Leads);

public sealed record RegiaoMetricasPainel(string Regiao, int Leads);

public sealed record ImovelMetricasPainel(string Id, string Bairro, int Conversas);

public sealed record HorarioMetricasPainel(DateTimeOffset Inicio, string Iniciais);

public sealed record PrivacidadeMetricasPainel(
    int Leads,
    int ComConsentimento,
    int PrazoRetencaoMeses,
    int Vencem30Dias,
    DateTimeOffset? ProximoVencimento);
