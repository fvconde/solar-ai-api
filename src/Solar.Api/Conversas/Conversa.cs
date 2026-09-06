using Solar.Api.Contracts;

namespace Solar.Api.Conversas;

public sealed class Conversa(Guid id)
{
    private readonly List<MensagemHistorico> _mensagens = [];

    public Guid Id { get; } = id;

    public PerfilLead Perfil { get; private set; } = new();

    public IReadOnlyList<MensagemHistorico> Mensagens => _mensagens;

    public IReadOnlyList<MensagemHistorico> HistoricoRecente(int janela) =>
        _mensagens.Count <= janela
            ? [.. _mensagens]
            : _mensagens[^janela..];

    public void RegistrarTurno(string mensagemDoLead, TurnoResponse turno, DateTimeOffset em)
    {
        _mensagens.Add(new MensagemHistorico(Papeis.Lead, mensagemDoLead, em));
        _mensagens.Add(new MensagemHistorico(Papeis.Agente, turno.Resposta, em));

        Perfil = Fundir(Perfil, turno.Intencao, turno.CamposExtraidos);
    }

    private static PerfilLead Fundir(PerfilLead atual, string intencao, CamposExtraidos extraidos) =>
        new(
            Nome: extraidos.Nome ?? atual.Nome,
            Intencao: intencao is Intencoes.Indefinida or "" ? atual.Intencao : intencao,
            PrecoMin: extraidos.PrecoMin ?? atual.PrecoMin,
            PrecoMax: extraidos.PrecoMax ?? atual.PrecoMax,
            Quartos: extraidos.Quartos ?? atual.Quartos,
            Regiao: extraidos.Regiao ?? atual.Regiao,
            Urgencia: extraidos.Urgencia ?? atual.Urgencia,
            ExpectativaRetorno: extraidos.ExpectativaRetorno ?? atual.ExpectativaRetorno,
            Score: extraidos.Score ?? atual.Score);
}
