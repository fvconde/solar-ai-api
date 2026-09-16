using Solar.Api.Agente;
using Solar.Api.Contracts;
using Solar.Api.Persistencia;

namespace Solar.Api.Conversas;

public sealed class ServicoDeReengajamento(
    IServiceScopeFactory scopeFactory,
    TravaDeConversas travas,
    IConfiguration configuracao,
    ILogger<ServicoDeReengajamento> logger) : BackgroundService
{
    private const int JanelaPadrao = 20;

    public TimeSpan IntervaloInatividade =>
        configuracao.GetValue("FollowUp:IntervaloInatividade", TimeSpan.FromHours(2));

    public TimeSpan IntervaloVarredura =>
        configuracao.GetValue("FollowUp:IntervaloVarredura", TimeSpan.FromMinutes(15));

    public int LimiteTentativas =>
        configuracao.GetValue("FollowUp:LimiteTentativas", 2);

    private int Janela => Math.Clamp(
        configuracao.GetValue("Conversas:JanelaHistorico", JanelaPadrao),
        2,
        ContratoTurno.LimiteHistorico);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(IntervaloVarredura);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExecutarCicloAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na execucao do ciclo de reengajamento");
            }
        }
    }

    public async Task<int> ExecutarCicloAsync(CancellationToken cancellationToken = default)
    {
        var agora = DateTimeOffset.UtcNow;
        var corteInatividade = agora - IntervaloInatividade;
        List<Guid> ids;

        using (var scope = scopeFactory.CreateScope())
        {
            var conversas = scope.ServiceProvider.GetRequiredService<ConversaRepositorio>();
            ids = await conversas.ObterIdsInativasParaFollowUpAsync(corteInatividade, LimiteTentativas, cancellationToken);
        }

        var processadas = 0;

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using var _ = await travas.TravarAsync(id, cancellationToken);
                using var scope = scopeFactory.CreateScope();
                var conversas = scope.ServiceProvider.GetRequiredService<ConversaRepositorio>();
                var agente = scope.ServiceProvider.GetRequiredService<AgenteClient>();

                var conversa = await conversas.ObterParaEscritaAsync(id, cancellationToken);
                if (conversa is null || conversa.Lead.TemConsentimento != true)
                {
                    continue;
                }

                if (conversa.AtualizadaEm > corteInatividade || conversa.TentativasReengajamento >= LimiteTentativas)
                {
                    continue;
                }

                if (conversa.Desfecho == "encerrar" || conversa.Desfecho == "agendar_reuniao" || conversa.Desfecho == "direcionar_especialista")
                {
                    continue;
                }

                var historico = await conversas.HistoricoRecenteAsync(id, Janela, cancellationToken);
                var turno = new TurnoRequest(
                    id,
                    "[reengajar]",
                    historico,
                    conversa.Lead.ParaContrato(),
                    []);

                TurnoResponse resposta;
                try
                {
                    resposta = await agente.ReengajarAsync(turno, cancellationToken);
                }
                catch (AgenteIndisponivelException erro)
                {
                    logger.LogWarning(erro, "Agente indisponivel durante follow-up da conversa {ConversaId}", id);
                    continue;
                }

                var gravadoEm = DateTimeOffset.UtcNow;
                await conversas.GravarFollowUpAsync(conversa, resposta, gravadoEm, cancellationToken);
                processadas++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Erro inesperado ao processar follow-up da conversa {ConversaId}", id);
            }
        }

        return processadas;
    }
}
