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
            .Where(corretor => corretor.Perfil == PerfisDoPainel.Corretor
                && corretor.StatusCorretor == StatusDoCorretor.Aprovado
                && corretor.Especialidades.Contains(especialidade))
            .Select(corretor => new
            {
                corretor.Id,
                corretor.Nome,
                corretor.Especialidades,
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
                corretor.Especialidades,
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

    public Task<Encaminhamento?> ObterAsync(long id, CancellationToken cancellationToken) =>
        db.Encaminhamentos
            .AsNoTracking()
            .FirstOrDefaultAsync(encaminhamento => encaminhamento.Id == id, cancellationToken);

    public Task<Encaminhamento?> ObterParaEscritaAsync(long id, CancellationToken cancellationToken) =>
        db.Encaminhamentos
            .FirstOrDefaultAsync(encaminhamento => encaminhamento.Id == id, cancellationToken);

    public async Task GravarResumoAsync(
        Encaminhamento encaminhamento,
        ResumoResponse resumo,
        CancellationToken cancellationToken)
    {
        encaminhamento.RegistrarResumo(resumo);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RedistribuirAsync(
        Guid corretorId,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        var encaminhamentos = await db.Encaminhamentos
            .Where(e => e.CorretorId == corretorId)
            .OrderBy(e => e.Em)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        if (encaminhamentos.Count == 0)
        {
            return;
        }

        var conversaIds = encaminhamentos.Select(e => e.ConversaId).ToArray();
        var conversas = await db.Conversas
            .Include(c => c.Lead)
            .Where(c => conversaIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        foreach (var encaminhamento in encaminhamentos)
        {
            encaminhamento.Desatribuir(em);
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var encaminhamento in encaminhamentos)
        {
            var conversa = conversas[encaminhamento.ConversaId];
            var corretores = await CandidatosAsync(encaminhamento.Especialidade, cancellationToken);
            var escolhido = EscolhaDeCorretor.EscolherPorEspecialidade(
                encaminhamento.Especialidade,
                conversa.Lead.Regiao,
                corretores);
            encaminhamento.Atribuir(escolhido, em);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<CorretorCandidato>> CandidatosAsync(
        string especialidade,
        CancellationToken cancellationToken)
    {
        var corretores = await db.Corretores
            .AsNoTracking()
            .Where(c => c.Perfil == PerfisDoPainel.Corretor
                && c.StatusCorretor == StatusDoCorretor.Aprovado
                && c.Especialidades.Contains(especialidade))
            .Select(c => new
            {
                c.Id,
                c.Especialidades,
                c.Regioes,
                c.Ativo,
                c.CriadoEm,
                Carga = db.Encaminhamentos.Count(e => e.CorretorId == c.Id),
                Ultimo = db.Encaminhamentos
                    .Where(e => e.CorretorId == c.Id)
                    .Max(e => (DateTimeOffset?)e.Em),
            })
            .ToListAsync(cancellationToken);

        return [.. corretores.Select(c => new CorretorCandidato(
            c.Id, c.Especialidades, c.Regioes, c.Ativo, c.Carga, c.Ultimo, c.CriadoEm))];
    }

    private async Task<AtribuicaoJaGravada?> AtribuidoAsync(Guid conversaId, CancellationToken cancellationToken) =>
        await db.Encaminhamentos
            .AsNoTracking()
            .Where(encaminhamento => encaminhamento.ConversaId == conversaId)
            .Select(encaminhamento => new AtribuicaoJaGravada(
                encaminhamento.Corretor == null ? null : encaminhamento.Corretor.Nome))
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record AtribuicaoJaGravada(string? Corretor);
}
