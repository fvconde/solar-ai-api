using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Conversas;

/// <summary>
/// Le e grava conversas no Postgres. Substitui o dicionario em memoria do S-07.
/// </summary>
public sealed class ConversaRepositorio(SolarDbContext db)
{
    /// <summary>
    /// Traz a conversa com o lead, ou cria uma nova <b>sem gravar</b>. A conversa
    /// nova fica apenas rastreada pelo EF: quem a leva ao banco e o
    /// <see cref="RegistrarTurnoAsync"/>. E o que preserva a invariante de que
    /// turno que falha nao deixa rastro -- nem conversa vazia.
    /// </summary>
    public async Task<Conversa> ObterOuCriarAsync(Guid id, DateTimeOffset em, CancellationToken cancellationToken)
    {
        var existente = await CarregarAsync(id, rastrear: true, cancellationToken);

        if (existente is not null)
        {
            return existente;
        }

        var nova = Conversa.Nova(id, Canais.Web, em);

        db.Conversas.Add(nova);

        return nova;
    }

    public Task<Conversa?> ObterAsync(Guid id, CancellationToken cancellationToken) =>
        CarregarAsync(id, rastrear: false, cancellationToken);

    /// <summary>
    /// Ultimas <paramref name="janela"/> mensagens, em ordem cronologica.
    ///
    /// <para>
    /// Em memoria isto era uma fatia da lista. No banco a diferenca importa: sem
    /// o <c>Take</c>, uma conversa longa carregaria o historico inteiro a cada
    /// turno so para descartar quase tudo. Ordena decrescente para o banco poder
    /// parar no indice, e inverte em memoria.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MensagemHistorico>> HistoricoRecenteAsync(
        Guid conversaId,
        int janela,
        CancellationToken cancellationToken)
    {
        var recentes = await db.Mensagens
            .AsNoTracking()
            .Where(m => m.ConversaId == conversaId)
            .OrderByDescending(m => m.Id)
            .Take(janela)
            .Select(m => new MensagemHistorico(m.Papel, m.Texto, m.Em))
            .ToListAsync(cancellationToken);

        recentes.Reverse();

        return recentes;
    }

    /// <summary>
    /// A conversa inteira para a UI redesenhar. Leva o <c>ProximaAcao</c>, que o
    /// <see cref="MensagemHistorico"/> do turno nao carrega.
    /// </summary>
    public Task<List<MensagemDaConversa>> HistoricoCompletoAsync(Guid conversaId, CancellationToken cancellationToken) =>
        db.Mensagens
            .AsNoTracking()
            .Where(m => m.ConversaId == conversaId)
            .OrderBy(m => m.Id)
            .Select(m => new MensagemDaConversa(m.Papel, m.Texto, m.Em, m.ProximaAcao))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Grava as duas mensagens do turno e o perfil fundido. Um unico
    /// <c>SaveChangesAsync</c>, que o EF ja envolve em transacao: ou entram as
    /// duas mensagens e o perfil novo, ou nao entra nada.
    /// </summary>
    public async Task RegistrarTurnoAsync(
        Conversa conversa,
        string mensagemDoLead,
        TurnoResponse turno,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        conversa.RegistrarTurno(mensagemDoLead, turno, em);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Sem <paramref name="rastrear"/> o EF guarda uma copia de cada entidade
    /// carregada para comparar no SaveChanges. O GET so le e devolve, entao paga
    /// esse custo a toa -- quem precisa do rastreamento e o caminho do turno,
    /// porque e ele que altera o lead.
    /// </summary>
    private Task<Conversa?> CarregarAsync(Guid id, bool rastrear, CancellationToken cancellationToken)
    {
        var consulta = db.Conversas.Include(c => c.Lead);

        return rastrear
            ? consulta.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            : consulta.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }
}
