using System.Text.Json.Serialization;

namespace Solar.Api.Contracts;

/// <summary>Identificação de um corretor ativo para seleção de perfil no painel.</summary>
public sealed record CorretorIdentificacao(
    Guid Id,
    string Nome,
    string Especialidade);

/// <summary>Corretor exibido nos cards de distribuição do painel.</summary>
public sealed record CorretorPainelResumo(
    Guid Id,
    string Nome,
    string Iniciais);

/// <summary>
/// Item da fila de leads. Nenhum dado de contato ou transcrição pertence à lista.
/// </summary>
public sealed record LeadPainelItem(
    Guid Id,
    string? NomeExibicao,
    string Referencia,
    string PedidoResumo,
    DateTimeOffset CriadoEm,
    int? Qualificacao,
    string LeadStatus,
    string? EncaminhamentoStatus,
    CorretorPainelResumo? Corretor)
{
    [JsonIgnore]
    public string? Nome => NomeExibicao;
}

/// <summary>Resposta da listagem autorizada da fila de leads.</summary>
public sealed record FilaLeadsResponse(
    IReadOnlyList<LeadPainelItem> Itens,
    int Total)
{
    // Compatibilidade de código com testes legados. Não é serializado: o contrato usa "itens".
    [JsonIgnore]
    public IReadOnlyList<LeadPainelItem> Leads => Itens;
}

public sealed record IdentificacaoPainelRequest(string? Email);

public sealed record IdentificacaoPainelResponse(bool Cadastrado);

public sealed record SessaoPainelRequest(string? Email, string? Senha);

public sealed record CorretorPainelResponse(Guid Id, string Nome, string Especialidade);

public sealed record SessaoPainelResponse(
    CorretorPainelResponse Corretor,
    string Perfil,
    Guid? CorretorId,
    bool VinculoAtivo,
    IReadOnlyList<string> FiltrosPermitidos,
    string FiltroInicial);

public sealed record ContatoPainelResponse(string? Telefone, string? Email);

public sealed record FatorQualificacaoPainelResponse(
    string Codigo,
    string Rotulo,
    int Pontos,
    bool Preenchido);

public sealed record QualificacaoPainelResponse(
    int? Valor,
    IReadOnlyList<FatorQualificacaoPainelResponse> Fatores);

public sealed record EncaminhamentoPainelResponse(
    long Id,
    string Status,
    CorretorPainelResumo? Corretor,
    DateTimeOffset AtribuidoEm);

public sealed record AgendamentoPainelResponse(
    DateTimeOffset DataHora,
    string Status);

public sealed record TranscricaoPainelResponse(
    string Papel,
    string Texto,
    DateTimeOffset Em);

public sealed record DetalheLeadPainelResponse(
    Guid Id,
    string? NomeExibicao,
    string Referencia,
    string PedidoResumo,
    DateTimeOffset CriadoEm,
    string LeadStatus,
    ContatoPainelResponse Contato,
    QualificacaoPainelResponse Qualificacao,
    ResumoResponse? Resumo,
    EncaminhamentoPainelResponse? Encaminhamento,
    AgendamentoPainelResponse? Agendamento,
    IReadOnlyList<ImovelSugerido> ImoveisSugeridos,
    IReadOnlyList<TranscricaoPainelResponse> Transcricao);

public sealed record PerfilInsuficientePainelResponse(
    string Erro,
    string PerfilExigido);

public sealed record TentativasRestantesResponse(int TentativasRestantes);

public sealed record BloqueadoPorSegundosResponse(int BloqueadoPorSegundos);

public sealed record RecuperacaoSenhaRequest(string? Email);

public sealed record RecuperacaoSenhaTokenResponse(string Email);

public sealed record NovaSenhaPainelRequest(string? Token, string? NovaSenha);

public sealed record ErroPainelResponse(string Erro);
