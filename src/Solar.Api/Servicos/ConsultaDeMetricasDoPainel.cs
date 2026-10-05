using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Servicos;

internal static class ConsultaDeMetricasDoPainel
{
    private const int QuantidadeRegioesNoTop = 5;

    public static MetricasPainelResponse Vazia(
        PeriodoMetricasPainel periodo, int prazoRetencaoMeses, bool supervisor, int janelaRespostaFollowUpDias)
    {
        IReadOnlyList<AvancoMetricasPainel> avanco = supervisor
            ? [
                new AvancoMetricasPainel("iniciadas", 0),
                new AvancoMetricasPainel("intencao", 0),
                new AvancoMetricasPainel("essenciais", 0),
                new AvancoMetricasPainel("encaminhamento", 0, SemEssenciais: 0),
                new AvancoMetricasPainel("corretor", 0),
                new AvancoMetricasPainel("horario", 0),
            ]
            : [
                new AvancoMetricasPainel("atribuidas", 0),
                new AvancoMetricasPainel("horario", 0),
            ];

        return new(
            periodo,
            ConversasIniciadas: 0,
            HorariosConfirmados: 0,
            ReservasProximos7Dias: 0,
            LeadsPorIntencao: new(0, 0, 0, 0),
            Equipe: supervisor ? new([], 0, 0) : null,
            Extras: new(
                Score: new(0, 0, 0, 0),
                Regioes: new([], 0, 0, 0),
                Imoveis: [],
                ProximosHorarios: [],
                Privacidade: new(0, 0, prazoRetencaoMeses, 0, null),
                TempoMedianoMin: null,
                TempoMedianoDiario: [],
                FollowUp: new FollowUpMetricasPainel(janelaRespostaFollowUpDias, 0, 0, 0, 0)),
            Avanco: avanco,
            DadosEssenciaisPreenchidos: 0);
    }

    public static async Task<MetricasPainelResponse> ObterAsync(
        SolarDbContext db,
        Guid? corretorId,
        bool supervisor,
        PeriodoMetricasPainel periodo,
        int prazoRetencaoMeses,
        int janelaRespostaFollowUpDias,
        CancellationToken cancellationToken)
    {
        var leads = await db.Leads.AsNoTracking().ToListAsync(cancellationToken);
        var encaminhamentos = await db.Encaminhamentos.AsNoTracking()
            .Include(e => e.Corretor)
            .ToListAsync(cancellationToken);
        var ultimoPorLead = RegrasDaFilaDeLeads.SelecionarUltimoEncaminhamentoPorLead(encaminhamentos);
        var filtro = supervisor ? "visao_geral" : "meus_leads";
        var autorizados = leads.Where(lead =>
        {
            ultimoPorLead.TryGetValue(lead.Id, out var encaminhamento);
            return RegrasDaFilaDeLeads.PertenceAoFiltro(lead, encaminhamento, filtro, corretorId);
        }).ToList();
        var leadIds = autorizados.Select(lead => lead.Id).ToArray();
        var porConversa = encaminhamentos.ToDictionary(e => e.ConversaId);
        var conversas = await db.Conversas.AsNoTracking()
            .Where(c => leadIds.Contains(c.LeadId))
            .Select(c => new
            {
                c.Id,
                c.IntencaoEm,
                c.EssenciaisEm,
                c.EncaminhadaEm,
                c.CorretorAtribuidoEm,
                c.PrimeiroReengajamentoEm,
            })
            .ToListAsync(cancellationToken);

        var conversasFiltradas = conversas.Where(c =>
            supervisor || (porConversa.TryGetValue(c.Id, out var encaminhamento) &&
                encaminhamento.CorretorId == corretorId)).ToList();
        var conversaIds = conversasFiltradas.Select(c => c.Id).ToArray();

        var primeirasMensagens = await db.Mensagens.AsNoTracking()
            .Where(m => conversaIds.Contains(m.ConversaId) && m.Papel == Papeis.Lead)
            .GroupBy(m => m.ConversaId)
            .Select(grupo => new { ConversaId = grupo.Key, Em = grupo.Min(m => m.Em) })
            .ToListAsync(cancellationToken);

        var iniciadasDict = primeirasMensagens
            .Where(m => m.Em >= periodo.Inicio && m.Em <= periodo.AtualizadoEm)
            .ToDictionary(m => m.ConversaId, m => m.Em);

        var iniciadasIds = iniciadasDict.Keys.ToArray();
        var conversasIniciadas = conversasFiltradas.Where(c => iniciadasDict.ContainsKey(c.Id)).ToList();

        var avancoIds = primeirasMensagens
            .Where(m => m.Em >= periodo.Inicio && m.Em <= periodo.AtualizadoEm && m.Em >= periodo.HistoricoDesde)
            .Select(m => m.ConversaId)
            .ToHashSet();

        var conversasAvanco = conversasFiltradas.Where(c => avancoIds.Contains(c.Id)).ToList();
        var dadosEssenciaisPreenchidos = conversasAvanco.Count(c => c.EssenciaisEm.HasValue && c.EssenciaisEm.Value <= periodo.AtualizadoEm);

        var avancoIdsArray = avancoIds.ToArray();
        var conversasComHorarioConfirmado = avancoIdsArray.Length == 0
            ? new HashSet<Guid>()
            : (await db.Mensagens.AsNoTracking()
                .Where(m => avancoIdsArray.Contains(m.ConversaId) &&
                    m.StatusAgendamento == EstadosDoAgendamento.Confirmado &&
                    m.Em <= periodo.AtualizadoEm)
                .Select(m => m.ConversaId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var totalHorariosConfirmados = conversasComHorarioConfirmado.Count;

        IReadOnlyList<AvancoMetricasPainel> avanco;
        if (supervisor)
        {
            var countIniciadas = conversasAvanco.Count;
            var countIntencao = conversasAvanco.Count(c => c.IntencaoEm.HasValue && c.IntencaoEm.Value <= periodo.AtualizadoEm);
            var countEssenciais = conversasAvanco.Count(c => c.EssenciaisEm.HasValue && c.EssenciaisEm.Value <= periodo.AtualizadoEm);
            var countEncaminhamento = conversasAvanco.Count(c => c.EncaminhadaEm.HasValue && c.EncaminhadaEm.Value <= periodo.AtualizadoEm);
            var semEssenciais = conversasAvanco.Count(c =>
                c.EncaminhadaEm.HasValue &&
                c.EncaminhadaEm.Value <= periodo.AtualizadoEm &&
                (!c.EssenciaisEm.HasValue || c.EssenciaisEm.Value > c.EncaminhadaEm.Value));
            var countCorretor = conversasAvanco.Count(c => c.CorretorAtribuidoEm.HasValue && c.CorretorAtribuidoEm.Value <= periodo.AtualizadoEm);
            var countHorario = totalHorariosConfirmados;

            avanco =
            [
                new AvancoMetricasPainel("iniciadas", countIniciadas),
                new AvancoMetricasPainel("intencao", countIntencao),
                new AvancoMetricasPainel("essenciais", countEssenciais),
                new AvancoMetricasPainel("encaminhamento", countEncaminhamento, SemEssenciais: semEssenciais),
                new AvancoMetricasPainel("corretor", countCorretor),
                new AvancoMetricasPainel("horario", countHorario),
            ];
        }
        else
        {
            avanco =
            [
                new AvancoMetricasPainel("atribuidas", conversasAvanco.Count),
                new AvancoMetricasPainel("horario", totalHorariosConfirmados),
            ];
        }

        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.LeadId.HasValue && leadIds.Contains(s.LeadId.Value) &&
                s.Inicio >= periodo.AtualizadoEm &&
                (supervisor || s.CorretorId == corretorId))
            .OrderBy(s => s.Inicio).ThenBy(s => s.Id)
            .Select(s => new { s.Inicio, Nome = s.Corretor.Nome })
            .ToListAsync(cancellationToken);

        EquipeMetricasPainel? equipe = null;
        if (supervisor)
        {
            var ids = conversaIds.ToHashSet();
            var atuais = encaminhamentos.Where(e => ids.Contains(e.ConversaId)).ToList();
            var atribuicoes = atuais.Where(e => e.CorretorId is not null)
                .GroupBy(e => e.CorretorId!.Value)
                .Select(grupo => new AtribuicaoMetricasPainel(
                    ResumoDoCorretor(grupo.First().Corretor!), grupo.Count()))
                .OrderByDescending(item => item.Conversas)
                .ThenBy(item => item.Corretor.Id).ToList();
            var pendentes = await db.Corretores.AsNoTracking().CountAsync(c =>
                c.Perfil == PerfisDoPainel.Corretor &&
                c.StatusCorretor == StatusDoCorretor.EmAnalise && c.Ativo, cancellationToken);
            equipe = new(atribuicoes,
                atuais.Count(e => e.CorretorId is null), pendentes);
        }

        var regioes = autorizados.Where(lead => !string.IsNullOrWhiteSpace(lead.Regiao))
            .GroupBy(lead => NormalizarRegiao(lead.Regiao!))
            .Select(grupo => new RegiaoMetricasPainel(grupo.Key, grupo.Count()))
            .OrderByDescending(item => item.Leads)
            .ThenBy(item => item.Regiao, StringComparer.Ordinal).ToList();
        var snapshots = await db.Mensagens.AsNoTracking()
            .Where(m => conversaIds.Contains(m.ConversaId) && m.ImoveisSugeridos != null)
            .Select(m => new { m.ConversaId, m.Em, m.Id, m.ImoveisSugeridos })
            .ToListAsync(cancellationToken);
        var imoveis = snapshots.SelectMany(m => m.ImoveisSugeridos!.Select(imovel =>
                new { m.ConversaId, m.Em, MensagemId = m.Id, imovel.Id, imovel.Bairro }))
            .GroupBy(imovel => imovel.Id, StringComparer.Ordinal)
            .Select(grupo => new ImovelMetricasPainel(
                grupo.Key,
                grupo.OrderByDescending(item => item.Em)
                    .ThenByDescending(item => item.MensagemId)
                    .ThenBy(item => item.Bairro, StringComparer.Ordinal).First().Bairro,
                grupo.Select(item => item.ConversaId).Distinct().Count()))
            .OrderByDescending(item => item.Conversas)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToList();

        var contatos = await ConsultaDeUltimoContato.ObterAsync(
            db, db.Leads.AsNoTracking().Where(lead => leadIds.Contains(lead.Id)), cancellationToken);
        var vencimentos = contatos.Select(contato => contato.Em.AddMonths(prazoRetencaoMeses)).ToList();

        var temposValidos = new List<(double Duracao, DateTimeOffset EncaminhadaEm)>();
        foreach (var c in conversasIniciadas)
        {
            var primeiraMensagemReal = iniciadasDict[c.Id];
            if (c.EncaminhadaEm.HasValue &&
                c.EncaminhadaEm.Value <= periodo.AtualizadoEm &&
                c.EncaminhadaEm.Value >= primeiraMensagemReal)
            {
                var duracao = (c.EncaminhadaEm.Value - primeiraMensagemReal).TotalMinutes;
                temposValidos.Add((duracao, c.EncaminhadaEm.Value));
            }
        }

        var duracoesGerais = temposValidos.Select(t => t.Duracao).ToList();
        var tempoMedianoMin = CalcularMediana(duracoesGerais);

        var hojeUtc = periodo.AtualizadoEm.UtcDateTime.Date;
        var diasSete = Enumerable.Range(0, 7).Select(i => hojeUtc.AddDays(-6 + i)).ToArray();
        var diaInicioUtc = diasSete[0];
        var diaFimUtc = diasSete[6];

        var temposNaJanela = temposValidos
            .Where(t => t.EncaminhadaEm.UtcDateTime.Date >= diaInicioUtc && t.EncaminhadaEm.UtcDateTime.Date <= diaFimUtc)
            .ToList();

        IReadOnlyList<double?> tempoMedianoDiario;
        if (temposNaJanela.Count == 0)
        {
            tempoMedianoDiario = [];
        }
        else
        {
            var porDia = temposNaJanela
                .GroupBy(t => t.EncaminhadaEm.UtcDateTime.Date)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Duracao).ToList());

            tempoMedianoDiario = diasSete.Select(dia =>
                porDia.TryGetValue(dia, out var lista) ? CalcularMediana(lista) : null).ToArray();
        }

        var conversasComFollowUp = conversasIniciadas
            .Where(c => c.PrimeiroReengajamentoEm.HasValue && c.PrimeiroReengajamentoEm.Value <= periodo.AtualizadoEm)
            .ToList();

        var comFollowUp = conversasComFollowUp.Count;

        var conversasEncerradas = conversasComFollowUp
            .Where(c => c.PrimeiroReengajamentoEm!.Value.AddDays(janelaRespostaFollowUpDias) <= periodo.AtualizadoEm)
            .ToList();

        var janelaEncerrada = conversasEncerradas.Count;
        var emObservacao = comFollowUp - janelaEncerrada;

        var encerradasIds = conversasEncerradas.Select(c => c.Id).ToArray();
        int responderam = 0;
        if (encerradasIds.Length > 0)
        {
            var mensagensLeadEncerradas = await db.Mensagens.AsNoTracking()
                .Where(m => encerradasIds.Contains(m.ConversaId) && m.Papel == Papeis.Lead)
                .Select(m => new { m.ConversaId, m.Em })
                .ToListAsync(cancellationToken);

            var mensagensPorConversa = mensagensLeadEncerradas
                .GroupBy(m => m.ConversaId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.Em).ToList());

            responderam = conversasEncerradas.Count(c =>
            {
                var inicioJanela = c.PrimeiroReengajamentoEm!.Value;
                var fimJanela = inicioJanela.AddDays(janelaRespostaFollowUpDias);
                return mensagensPorConversa.TryGetValue(c.Id, out var listaEm) &&
                       listaEm.Any(em => em > inicioJanela && em <= fimJanela);
            });
        }

        var followUp = new FollowUpMetricasPainel(
            janelaRespostaFollowUpDias,
            comFollowUp,
            janelaEncerrada,
            responderam,
            emObservacao);

        return new(periodo, iniciadasIds.Length, totalHorariosConfirmados,
            slots.Count(s => s.Inicio <= periodo.AtualizadoEm.AddDays(7)),
            new(autorizados.Count(lead => lead.Intencao == Intencoes.Compra),
                autorizados.Count(lead => lead.Intencao == Intencoes.Aluguel),
                autorizados.Count(lead => lead.Intencao == Intencoes.Investimento),
                autorizados.Count(lead => lead.Intencao is null or "" or Intencoes.Indefinida)),
            equipe,
            new(new(autorizados.Count(lead => lead.Score is >= 0 and <= 39),
                    autorizados.Count(lead => lead.Score is >= 40 and <= 69),
                    autorizados.Count(lead => lead.Score is >= 70 and <= 100),
                    autorizados.Count(lead => lead.Score is null)),
                new(regioes.Take(QuantidadeRegioesNoTop).ToList(),
                    regioes.Skip(QuantidadeRegioesNoTop).Sum(item => item.Leads),
                    regioes.Sum(item => item.Leads), autorizados.Count),
                imoveis,
                slots.Select(s => new HorarioMetricasPainel(s.Inicio, IniciaisDe(s.Nome))).ToList(),
                new(autorizados.Count,
                    autorizados.Count(lead => lead.ConsentimentoEm is not null),
                    prazoRetencaoMeses,
                    vencimentos.Count(em => em >= periodo.AtualizadoEm &&
                        em <= periodo.AtualizadoEm.AddDays(30)),
                    vencimentos.Where(em => em >= periodo.AtualizadoEm)
                        .Select(em => (DateTimeOffset?)em).Min()),
                TempoMedianoMin: tempoMedianoMin,
                TempoMedianoDiario: tempoMedianoDiario,
                FollowUp: followUp),
            avanco,
            dadosEssenciaisPreenchidos);
    }

    private static string NormalizarRegiao(string regiao)
    {
        var normalizada = regiao.Trim().Normalize(NormalizationForm.FormD);
        var semAcentos = new string(normalizada.Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return semAcentos.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static CorretorPainelResumo ResumoDoCorretor(Corretor corretor) =>
        new(corretor.Id, corretor.Nome, IniciaisDe(corretor.Nome));

    private static string IniciaisDe(string nome)
    {
        var partes = nome.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length switch
        {
            0 => string.Empty,
            1 => partes[0][..Math.Min(2, partes[0].Length)].ToUpperInvariant(),
            _ => (partes[0][..1] + partes[^1][..1]).ToUpperInvariant(),
        };
    }

    private static double? CalcularMediana(List<double> valores)
    {
        if (valores.Count == 0)
        {
            return null;
        }

        valores.Sort();
        var n = valores.Count;
        if (n % 2 == 1)
        {
            return valores[n / 2];
        }

        return (valores[(n / 2) - 1] + valores[n / 2]) / 2.0;
    }
}
