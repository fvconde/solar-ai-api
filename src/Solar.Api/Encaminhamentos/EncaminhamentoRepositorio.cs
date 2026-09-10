using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Encaminhamentos;

/// <summary>O que o handoff produziu: a linha a gravar e o nome que a trilha mostra.</summary>
public sealed record AtribuicaoDoHandoff(Encaminhamento? Novo, string? Corretor);

public sealed class EncaminhamentoRepositorio(SolarDbContext db)
{
    public static bool EhHandoff(string? proximaAcao) =>
        proximaAcao is ProximasAcoes.AgendarReuniao or ProximasAcoes.DirecionarEspecialista;

    /// <summary>
    /// Escolhe o corretor <b>sem gravar</b>: a linha volta so rastreada, e quem a
    /// leva ao banco e o mesmo <c>SaveChanges</c> do turno. E o que mantem a
    /// invariante do S-10 -- turno que falha nao deixa encaminhamento.
    /// </summary>
    public async Task<AtribuicaoDoHandoff> DecidirAsync(
        Conversa conversa,
        string proximaAcao,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        if (!EhHandoff(proximaAcao))
        {
            return new AtribuicaoDoHandoff(null, null);
        }

        var existente = await AtribuidoAsync(conversa.Id, cancellationToken);

        if (existente is not null)
        {
            return new AtribuicaoDoHandoff(null, existente.Corretor);
        }

        var especialidade = EscolhaDeCorretor.EspecialidadeDe(conversa.Lead.Intencao);

        var corretores = await db.Corretores
            .AsNoTracking()
            .Where(corretor => corretor.Especialidade == especialidade)
            .Select(corretor => new
            {
                corretor.Id,
                corretor.Nome,
                corretor.Especialidade,
                corretor.Regioes,
                corretor.Ativo,
                corretor.CriadoEm,
                Carga = db.Encaminhamentos.Count(e => e.CorretorId == corretor.Id),
                Ultimo = db.Encaminhamentos
                    .Where(e => e.CorretorId == corretor.Id)
                    .Max(e => (DateTimeOffset?)e.Em),
            })
            .ToListAsync(cancellationToken);

        var escolhido = EscolhaDeCorretor.Escolher(
            conversa.Lead.Intencao,
            conversa.Lead.Regiao,
            [.. corretores.Select(corretor => new CorretorCandidato(
                corretor.Id,
                corretor.Especialidade,
                corretor.Regioes,
                corretor.Ativo,
                corretor.Carga,
                corretor.Ultimo,
                corretor.CriadoEm))]);

        conversa.Lead.MarcarEncaminhado(em);

        var encaminhamento = Encaminhamento.Novo(
            conversa.Id, conversa.LeadId, escolhido, especialidade, em);

        return new AtribuicaoDoHandoff(
            encaminhamento,
            corretores.FirstOrDefault(corretor => corretor.Id == escolhido)?.Nome);
    }

    /// <summary>Nome do corretor ja atribuido a conversa, para o GET redesenhar a trilha.</summary>
    public async Task<string?> CorretorDaConversaAsync(Guid conversaId, CancellationToken cancellationToken) =>
        (await AtribuidoAsync(conversaId, cancellationToken))?.Corretor;

    private async Task<AtribuicaoJaGravada?> AtribuidoAsync(Guid conversaId, CancellationToken cancellationToken) =>
        await db.Encaminhamentos
            .AsNoTracking()
            .Where(encaminhamento => encaminhamento.ConversaId == conversaId)
            .Select(encaminhamento => new AtribuicaoJaGravada(
                encaminhamento.Corretor == null ? null : encaminhamento.Corretor.Nome))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record AtribuicaoJaGravada(string? Corretor);
}
