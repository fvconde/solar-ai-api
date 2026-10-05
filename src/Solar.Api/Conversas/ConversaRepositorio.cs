using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Conversas;

/// <summary>
/// Le e grava conversas no Postgres. Substitui o dicionario em memoria do S-07.
/// </summary>
public sealed class ConversaRepositorio(SolarDbContext db)
{
    /// <summary>
    /// Traz a conversa com o lead, ou cria uma nova <b>sem gravar</b>. A conversa
    /// nova fica apenas rastreada pelo EF: quem a leva ao banco e o
    /// <see cref="GravacaoDoTurno"/>. E o que preserva a invariante de que
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

    public Task<Conversa?> ObterParaEscritaAsync(Guid id, CancellationToken cancellationToken) =>
        CarregarAsync(id, rastrear: true, cancellationToken);

    /// <summary>
    /// Vincula uma conversa ainda sem dono. Se o lead tambem pertencer a
    /// conversas sem dono ou de outra conta, cria uma copia isolada antes de
    /// transferir a posse.
    /// </summary>
    public async Task VincularContaAsync(
        Guid conversaId,
        Guid contaId,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        var conversa = await ObterParaEscritaAsync(conversaId, cancellationToken);
        if (conversa is null || conversa.ContaId is not null)
        {
            return;
        }

        await VincularContaAsync(conversa, contaId, em, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task VincularContaAsync(
        Conversa conversa,
        Guid contaId,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        if (conversa.ContaId is not null)
        {
            return;
        }

        var leadCompartilhado = await db.Conversas
            .AnyAsync(c => c.LeadId == conversa.LeadId
                && c.Id != conversa.Id
                && c.ContaId != contaId, cancellationToken);

        if (leadCompartilhado)
        {
            var copia = conversa.Lead.Clonar(em);
            db.Leads.Add(copia);
            conversa.ReapontarLead(copia);

            var encaminhamentos = await db.Encaminhamentos
                .Where(e => e.ConversaId == conversa.Id)
                .ToListAsync(cancellationToken);
            foreach (var encaminhamento in encaminhamentos)
            {
                encaminhamento.ReapontarLead(copia.Id);
            }
        }

        conversa.VincularConta(contaId);
    }

    public async Task<Conversa> RegistrarConsentimentoAsync(
        Guid id,
        string versaoAvisoPrivacidade,
        DateTimeOffset em,
        CancellationToken cancellationToken,
        Guid? contaId = null,
        byte[]? chaveExclusaoHash = null)
    {
        var conversa = await ObterOuCriarAsync(id, em, cancellationToken);

        if (contaId is not null && conversa.ContaId is null)
        {
            if (db.Entry(conversa).State == EntityState.Added)
            {
                conversa.VincularConta(contaId.Value);
            }
            else
            {
                await VincularContaAsync(conversa, contaId.Value, em, cancellationToken);
            }
        }

        if (db.Entry(conversa).State == EntityState.Added
            && conversa.ContaId is null
            && chaveExclusaoHash is not null)
        {
            conversa.DefinirChaveExclusaoHash(chaveExclusaoHash);
        }

        conversa.Lead.RegistrarConsentimento(versaoAvisoPrivacidade, em);
        await db.SaveChangesAsync(cancellationToken);

        return conversa;
    }

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
            .Select(m => new MensagemHistorico(m.Papel, m.Texto, m.Em, m.ImoveisSugeridos))
            .ToListAsync(cancellationToken);

        recentes.Reverse();

        return recentes;
    }

    /// <summary>
    /// A conversa inteira para a UI redesenhar. Leva o <c>ProximaAcao</c>, que o
    /// <see cref="MensagemHistorico"/> do turno nao carrega.
    /// </summary>
    public async Task<List<MensagemDaConversa>> HistoricoCompletoAsync(
        Guid conversaId,
        string? corretor,
        IReadOnlyList<SlotOferecido> agendaAtual,
        CancellationToken cancellationToken)
    {
        var registros = await db.Mensagens
            .AsNoTracking()
            .Where(m => m.ConversaId == conversaId)
            .OrderBy(m => m.Id)
            .Select(m => new
            {
                m.Papel,
                m.Texto,
                m.Em,
                m.ProximaAcao,
                m.StatusAgendamento,
                m.ImoveisSugeridos,
                Horario = m.Slot == null
                    ? null
                    : new SlotOferecido(m.Slot.Id, m.Slot.Inicio, m.Slot.Fim),
            })
            .ToListAsync(cancellationToken);

        var mensagens = registros.Select(mensagem => new MensagemDaConversa(
            mensagem.Papel,
            mensagem.Texto,
            mensagem.Em,
            mensagem.ProximaAcao,
            null,
            mensagem.StatusAgendamento is null
                ? null
                : new AgendamentoDaConversa(
                    mensagem.StatusAgendamento,
                    mensagem.Horario,
                    mensagem.StatusAgendamento == EstadosDoAgendamento.Indisponivel
                        ? agendaAtual
                        : []),
            mensagem.ImoveisSugeridos))
            .ToList();

        if (corretor is null)
        {
            return mensagens;
        }

        return [.. mensagens.Select(m => EncaminhamentoRepositorio.EhHandoff(m.ProximaAcao)
            ? m with { Corretor = corretor }
            : m)];
    }

    /// <summary>
    /// Aplica o turno em memoria. Separado da gravacao porque a escolha do
    /// corretor le o perfil ja fundido -- intencao e regiao deste turno.
    /// </summary>
    public void AplicarTurno(Conversa conversa, string mensagemDoLead, TurnoResponse turno, DateTimeOffset em) =>
        conversa.RegistrarTurno(mensagemDoLead, turno, em);

    /// <summary>
    /// Grava o contato e resolve a dedupe: o mesmo telefone ou e-mail visto em
    /// outra conversa traz esta conversa para o lead que ja existe, e o lead
    /// provisorio desta conversa deixa de existir. E o que transforma base de
    /// conversas em base de clientes.
    /// </summary>
    public async Task<Guid> RegistrarContatoAsync(
        Conversa conversa,
        ContatoRequest dados,
        DateTimeOffset em,
        CancellationToken cancellationToken)
    {
        var canonico = await CanonicoAsync(conversa, dados, cancellationToken);

        if (canonico is null || canonico.Id == conversa.LeadId)
        {
            conversa.Lead.RegistrarContato(dados.Nome, dados.Telefone, dados.Email, em);

            await db.SaveChangesAsync(cancellationToken);

            return conversa.LeadId;
        }

        var orfao = conversa.Lead;

        canonico.Absorver(orfao, em);
        canonico.RegistrarContato(dados.Nome, dados.Telefone, dados.Email, em);

        foreach (var encaminhamento in await db.Encaminhamentos
            .Where(e => e.LeadId == orfao.Id)
            .ToListAsync(cancellationToken))
        {
            encaminhamento.ReapontarLead(canonico.Id);
        }

        foreach (var outra in await db.Conversas
            .Where(c => c.LeadId == orfao.Id)
            .ToListAsync(cancellationToken))
        {
            outra.ReapontarLead(canonico);
        }

        db.Leads.Remove(orfao);

        await db.SaveChangesAsync(cancellationToken);

        return canonico.Id;
    }

    private async Task<Lead?> CanonicoAsync(
        Conversa conversa,
        ContatoRequest dados,
        CancellationToken cancellationToken)
    {
        var leadsDaMesmaConta = db.Leads.AsQueryable();
        if (conversa.ContaId is { } contaId)
        {
            leadsDaMesmaConta = leadsDaMesmaConta.Where(lead => !db.Conversas.Any(outra =>
                outra.LeadId == lead.Id && (outra.ContaId == null || outra.ContaId != contaId)));
        }
        else
        {
            leadsDaMesmaConta = leadsDaMesmaConta.Where(lead => !db.Conversas.Any(outra =>
                outra.LeadId == lead.Id && outra.ContaId != null));
        }

        var telefone = Contato.Telefone(dados.Telefone);

        if (telefone is not null)
        {
            var porTelefone = await leadsDaMesmaConta
                .FirstOrDefaultAsync(l => l.Telefone == telefone, cancellationToken);

            if (porTelefone is not null)
            {
                return porTelefone;
            }
        }

        var email = Contato.Email(dados.Email);

        return email is null
            ? null
            : await leadsDaMesmaConta.FirstOrDefaultAsync(l => l.Email == email, cancellationToken);
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

    public async Task<ExclusaoTitularResultado> ExcluirPeloTitularAsync(
        Guid conversaId,
        Guid leadInspecionadoId,
        IReadOnlyCollection<Guid> conversasInspecionadas,
        ProvaExclusaoTitular prova,
        CancellationToken cancellationToken)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM leads WHERE id = {leadInspecionadoId} FOR UPDATE", cancellationToken);
            var atuais = await db.Conversas.FromSqlInterpolated(
                    $"SELECT * FROM conversas WHERE lead_id = {leadInspecionadoId} OR id = {conversaId} ORDER BY id FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            var conversa = atuais.SingleOrDefault(c => c.Id == conversaId);
            if (conversa is null)
                return new(EstadoExclusaoTitular.NaoEncontrada);
            if (!prova.Autoriza(conversa))
                return new(EstadoExclusaoTitular.NaoAutorizada);
            if (conversa.LeadId != leadInspecionadoId
                || !atuais.Select(c => c.Id).ToHashSet().SetEquals(conversasInspecionadas))
                return new(EstadoExclusaoTitular.Conflito);

            var excluirLead = atuais.Count == 1;
            DateTimeOffset removidoEm;
            if (excluirLead)
            {
                var resultado = await ExcluirLeadAsync(conversa.LeadId, cancellationToken);
                if (resultado is null)
                    return new(EstadoExclusaoTitular.NaoEncontrada);
                removidoEm = resultado.RemovidoEm;
            }
            else
            {
                var resultado = await ExcluirApenasConversaAsync(conversaId, cancellationToken);
                if (resultado is null)
                    return new(EstadoExclusaoTitular.NaoEncontrada);
                removidoEm = resultado.RemovidoEm;
            }

            await transacao.CommitAsync(cancellationToken);
            return new(EstadoExclusaoTitular.Excluida, excluirLead, removidoEm);
        }
        catch (Exception erro) when (ConflitoDeTransacao(erro))
        {
            return new(EstadoExclusaoTitular.Conflito);
        }
    }

    private static bool ConflitoDeTransacao(Exception erro) =>
        (erro as PostgresException ?? erro.InnerException as PostgresException)?.SqlState
            is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure;

    /// <summary>
    /// Elimina definitivamente um lead e todos os seus registros vinculados
    /// (conversas, mensagens, encaminhamentos) em atendimento a LGPD.
    /// </summary>
    public async Task<ExclusaoLeadResultado?> ExcluirLeadAsync(
        Guid leadId,
        CancellationToken cancellationToken)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken);

        if (lead is null)
        {
            return null;
        }

        var conversas = await db.Conversas
            .Where(c => c.LeadId == leadId)
            .ToListAsync(cancellationToken);

        var conversaIds = conversas.Select(c => c.Id).ToList();

        var mensagens = await db.Mensagens
            .Where(m => conversaIds.Contains(m.ConversaId))
            .ToListAsync(cancellationToken);

        var encaminhamentos = await db.Encaminhamentos
            .Where(e => e.LeadId == leadId || conversaIds.Contains(e.ConversaId))
            .ToListAsync(cancellationToken);

        var totalMensagens = mensagens.Count;
        var qtdConversas = conversas.Count;

        db.Mensagens.RemoveRange(mensagens);
        db.Encaminhamentos.RemoveRange(encaminhamentos);
        db.Conversas.RemoveRange(conversas);
        db.Leads.Remove(lead);

        await db.SaveChangesAsync(cancellationToken);

        return new ExclusaoLeadResultado(leadId, qtdConversas, totalMensagens, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Retorna os IDs de todas as conversas vinculadas a um lead.
    /// Utilizado para coordenar travas de concorrencia durante a exclusao de dados.
    /// </summary>
    public async Task<List<Guid>> ObterIdsDeConversasDoLeadAsync(
        Guid leadId,
        CancellationToken cancellationToken)
    {
        return await db.Conversas
            .AsNoTracking()
            .Where(c => c.LeadId == leadId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Elimina exclusivamente uma conversa especifica e seus registros dependentes
    /// (mensagens e encaminhamentos vinculados), preservando o lead e eventuais
    /// outras conversas vinculadas.
    /// </summary>
    public async Task<ExclusaoConversaResultado?> ExcluirApenasConversaAsync(
        Guid conversaId,
        CancellationToken cancellationToken)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, cancellationToken);

        if (conversa is null)
        {
            return null;
        }

        var mensagens = await db.Mensagens
            .Where(m => m.ConversaId == conversaId)
            .ToListAsync(cancellationToken);

        var encaminhamentos = await db.Encaminhamentos
            .Where(e => e.ConversaId == conversaId)
            .ToListAsync(cancellationToken);

        var totalMensagens = mensagens.Count;

        db.Mensagens.RemoveRange(mensagens);
        db.Encaminhamentos.RemoveRange(encaminhamentos);
        db.Conversas.Remove(conversa);

        await db.SaveChangesAsync(cancellationToken);

        return new ExclusaoConversaResultado(
            conversaId,
            conversa.LeadId,
            LeadExcluido: false,
            totalMensagens,
            DateTimeOffset.UtcNow,
            "apenas_conversa");
    }

    /// <summary>
    /// Elimina o lead vinculado a uma conversa e todos os seus registros vinculados em cascata.
    /// </summary>
    public async Task<ExclusaoLeadResultado?> ExcluirPorConversaAsync(
        Guid conversaId,
        CancellationToken cancellationToken)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, cancellationToken);

        if (conversa is null)
        {
            return null;
        }

        return await ExcluirLeadAsync(conversa.LeadId, cancellationToken);
    }

    public async Task<List<Guid>> ObterIdsInativasParaFollowUpAsync(
        DateTimeOffset corteInatividade,
        int limiteTentativas,
        CancellationToken cancellationToken)
    {
        return await db.Conversas
            .AsNoTracking()
            .Where(c => c.AtualizadaEm <= corteInatividade
                        && c.TentativasReengajamento < limiteTentativas
                        && (c.Desfecho == null || (c.Desfecho != "encerrar" && c.Desfecho != "agendar_reuniao" && c.Desfecho != "direcionar_especialista"))
                        && c.Lead.ConsentimentoEm != null
                        && !db.Encaminhamentos.Any(e => e.ConversaId == c.Id)
                    // Nunca reengajar uma conversa que ainda nao recebeu a primeira
                    // resposta da Lia: uma fala isolada do lead nao e inatividade.
                    && db.Mensagens.Any(m => m.ConversaId == c.Id && m.Papel == Papeis.Agente))
            .OrderBy(c => c.AtualizadaEm)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task GravarFollowUpAsync(
        Conversa conversa,
        TurnoResponse turno,
        DateTimeOffset agora,
        CancellationToken cancellationToken)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        conversa.RegistrarFollowUp(turno, agora);

        await db.SaveChangesAsync(cancellationToken);
        await transacao.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Devolve todos os imoveis sugeridos ao longo de toda a conversa, sem
    /// duplicar ID, preservando a ordem cronologica da primeira recomendacao.
    /// Utilizado pelo S-18 para compor a secao de imoveis de interesse no resumo.
    /// </summary>
    public async Task<IReadOnlyList<ImovelSugerido>> ObterImoveisSugeridosAsync(
        Guid conversaId,
        CancellationToken cancellationToken = default)
    {
        var mensagensComImoveis = await db.Mensagens
            .AsNoTracking()
            .Where(m => m.ConversaId == conversaId && m.ImoveisSugeridos != null)
            .OrderBy(m => m.Id)
            .Select(m => m.ImoveisSugeridos)
            .ToListAsync(cancellationToken);

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultado = new List<ImovelSugerido>();

        foreach (var lista in mensagensComImoveis)
        {
            if (lista is null) continue;
            foreach (var imovel in lista)
            {
                if (vistos.Add(imovel.Id))
                {
                    resultado.Add(imovel);
                }
            }
        }

        return resultado;
    }

    /// <summary>Alias semantico para <see cref="ObterImoveisSugeridosAsync"/>.</summary>
    public Task<IReadOnlyList<ImovelSugerido>> ObterImoveisSugeridosDaConversaAsync(
        Guid conversaId,
        CancellationToken cancellationToken = default) =>
        ObterImoveisSugeridosAsync(conversaId, cancellationToken);
}
