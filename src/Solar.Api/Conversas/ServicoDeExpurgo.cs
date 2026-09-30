using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Persistencia;

namespace Solar.Api.Conversas;

public sealed class ServicoDeExpurgo(
    IServiceScopeFactory scopeFactory,
    TravaDeConversas travas,
    IConfiguration configuracao,
    ILogger<ServicoDeExpurgo> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private const int PrazoPadraoEmMeses = 12;
    private static readonly TimeSpan IntervaloPadrao = TimeSpan.FromDays(1);

    public TimeSpan IntervaloVarredura
    {
        get
        {
            var intervalo = configuracao.GetValue("Expurgo:IntervaloVarredura", IntervaloPadrao);
            return intervalo > TimeSpan.Zero
                ? intervalo
                : throw new InvalidOperationException("Expurgo:IntervaloVarredura deve ser positivo.");
        }
    }

    public int PrazoRetencaoMeses
    {
        get
        {
            var prazo = configuracao.GetValue("Expurgo:PrazoRetencaoMeses", PrazoPadraoEmMeses);
            return prazo > 0
                ? prazo
                : throw new InvalidOperationException("Expurgo:PrazoRetencaoMeses deve ser positivo.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(IntervaloVarredura);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                await ExecutarCicloAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogError("Falha no ciclo de expurgo por prazo de retencao.");
            }
        }
    }

    public async Task<int> ExecutarCicloAsync(CancellationToken cancellationToken = default)
    {
        var dataLimite = timeProvider.GetUtcNow().AddMonths(-PrazoRetencaoMeses);
        var leadsExpurgados = 0;

        try
        {
            List<Guid> candidatos;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                candidatos = await ObterCandidatosAsync(db, dataLimite, cancellationToken);
            }

            foreach (var leadId in candidatos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    List<Guid> conversasAntesDaTrava;
                    using (var consultaScope = scopeFactory.CreateScope())
                    {
                        var db = consultaScope.ServiceProvider.GetRequiredService<SolarDbContext>();
                        conversasAntesDaTrava = await db.Conversas
                            .AsNoTracking()
                            .Where(conversa => conversa.LeadId == leadId)
                            .Select(conversa => conversa.Id)
                            .ToListAsync(cancellationToken);
                    }

                    using var trava = await travas.TravarMultiplasAsync(
                        conversasAntesDaTrava.Append(leadId),
                        cancellationToken);

                    using var scope = scopeFactory.CreateScope();
                    var dbAtual = scope.ServiceProvider.GetRequiredService<SolarDbContext>();
                    var idsDeConversasAtuais = await dbAtual.Conversas
                        .AsNoTracking()
                        .Where(conversa => conversa.LeadId == leadId)
                        .Select(conversa => conversa.Id)
                        .ToListAsync(cancellationToken);

                    var idsBloqueados = conversasAntesDaTrava.ToHashSet();
                    if (idsDeConversasAtuais.Any(id => !idsBloqueados.Contains(id)))
                    {
                        continue;
                    }

                    var aindaElegivel = await ObterCandidatosAsync(
                        dbAtual,
                        dataLimite,
                        cancellationToken,
                        leadId);
                    if (aindaElegivel.Count == 0)
                    {
                        continue;
                    }

                    var conversas = scope.ServiceProvider.GetRequiredService<ConversaRepositorio>();
                    if (await conversas.ExcluirLeadAsync(leadId, cancellationToken) is not null)
                    {
                        leadsExpurgados++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    logger.LogError("Falha ao processar um candidato de expurgo por retencao.");
                }
            }

            return leadsExpurgados;
        }
        finally
        {
            if (leadsExpurgados > 0)
            {
                logger.LogInformation(
                    "Expurgo por retencao: {QuantidadeLeads} leads removidos em {HorarioUtc}.",
                    leadsExpurgados,
                    timeProvider.GetUtcNow());
            }
        }
    }

    private static async Task<List<Guid>> ObterCandidatosAsync(
        SolarDbContext db,
        DateTimeOffset dataLimite,
        CancellationToken cancellationToken,
        Guid? somenteLeadId = null)
    {
        var consultaLeads = db.Leads.AsNoTracking();
        if (somenteLeadId.HasValue)
        {
            consultaLeads = consultaLeads.Where(lead => lead.Id == somenteLeadId.Value);
        }

        var leads = await consultaLeads
            .Select(lead => new { lead.Id, lead.CriadoEm })
            .ToListAsync(cancellationToken);

        if (leads.Count == 0)
        {
            return [];
        }

        var idsDosLeads = leads.Select(lead => lead.Id).ToArray();
        var primeiraConversaPorLead = await db.Conversas
            .AsNoTracking()
            .Where(conversa => idsDosLeads.Contains(conversa.LeadId))
            .GroupBy(conversa => conversa.LeadId)
            .Select(grupo => new { LeadId = grupo.Key, CriadaEm = grupo.Min(conversa => conversa.CriadaEm) })
            .ToDictionaryAsync(item => item.LeadId, item => item.CriadaEm, cancellationToken);

        var ultimaMensagemDoLeadPorLead = await (
            from mensagem in db.Mensagens.AsNoTracking()
            join conversa in db.Conversas.AsNoTracking() on mensagem.ConversaId equals conversa.Id
            where mensagem.Papel == Papeis.Lead && idsDosLeads.Contains(conversa.LeadId)
            group mensagem by conversa.LeadId into grupo
            select new { LeadId = grupo.Key, EnviadaEm = grupo.Max(mensagem => mensagem.Em) }
        ).ToDictionaryAsync(item => item.LeadId, item => item.EnviadaEm, cancellationToken);

        var candidatos = new List<Guid>();
        foreach (var lead in leads)
        {
            DateTimeOffset ultimoContato;
            if (ultimaMensagemDoLeadPorLead.TryGetValue(lead.Id, out var ultimaMensagemDoLead))
            {
                ultimoContato = ultimaMensagemDoLead;
            }
            else if (primeiraConversaPorLead.TryGetValue(lead.Id, out var primeiraConversa) &&
                     primeiraConversa < lead.CriadoEm)
            {
                ultimoContato = primeiraConversa;
            }
            else
            {
                ultimoContato = lead.CriadoEm;
            }

            if (ultimoContato <= dataLimite)
            {
                candidatos.Add(lead.Id);
            }
        }

        return candidatos;
    }
}
