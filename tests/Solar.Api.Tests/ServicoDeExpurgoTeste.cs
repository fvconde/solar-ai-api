using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public class ServicoDeExpurgoTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Configuracao_base_define_prazo_atraso_e_intervalo_e_desenvolvimento_declara_demo()
    {
        var caminhoApi = Path.Combine(LocalizarRaizDoRepositorio(), "src", "Solar.Api");
        var configuracaoBase = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(caminhoApi, "appsettings.json"))
            .Build();
        var configuracaoDesenvolvimento = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(caminhoApi, "appsettings.json"))
            .AddJsonFile(Path.Combine(caminhoApi, "appsettings.Development.json"))
            .Build();

        Assert.Equal(12, configuracaoBase.GetValue<int>("Expurgo:PrazoRetencaoMeses"));
        Assert.Equal(TimeSpan.FromMinutes(5), configuracaoBase.GetValue<TimeSpan>("Expurgo:AtrasoInicial"));
        Assert.Equal(TimeSpan.FromDays(1), configuracaoBase.GetValue<TimeSpan>("Expurgo:IntervaloVarredura"));
        Assert.Equal(1, configuracaoDesenvolvimento.GetValue<int>("Expurgo:PrazoRetencaoMeses"));
        Assert.Equal(TimeSpan.FromSeconds(10), configuracaoDesenvolvimento.GetValue<TimeSpan>("Expurgo:AtrasoInicial"));
        Assert.Equal(TimeSpan.FromSeconds(30), configuracaoDesenvolvimento.GetValue<TimeSpan>("Expurgo:IntervaloVarredura"));

        using var provedor = CriarProvedor();
        var configuracaoSemExpurgo = new ConfigurationBuilder().Build();
        var servico = CriarServico(provedor, configuracaoSemExpurgo, Agora);

        Assert.Equal(12, servico.PrazoRetencaoMeses);
        Assert.Equal(TimeSpan.FromMinutes(5), servico.AtrasoInicial);
        Assert.Equal(TimeSpan.FromDays(1), servico.IntervaloVarredura);
    }

    [Fact]
    public async Task Ciclo_expurga_pelo_ultimo_contato_do_lead_em_todas_conversas_e_ignora_follow_up()
    {
        var contatoAntigo = Agora.AddMonths(-13);
        var conversaAntiga = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo);
        conversaAntiga.RegistrarTurno("mensagem antiga", Resposta(), contatoAntigo);
        conversaAntiga.RegistrarFollowUp(Resposta(), Agora.AddDays(-2));

        var conversaRecente = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddDays(-2));
        conversaRecente.ReapontarLead(conversaAntiga.Lead);
        conversaRecente.RegistrarFollowUp(Resposta(), Agora.AddDays(-1));

        using var provedor = CriarProvedor();
        await SalvarConversasAsync(provedor, conversaAntiga, conversaRecente);
        var servico = CriarServico(provedor, CriarConfiguracao(), Agora);

        var expurgados = await servico.ExecutarCicloAsync();

        Assert.Equal(1, expurgados);
        Assert.Equal((0, 0, 0), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Ciclo_preserva_lead_com_mensagem_recente_em_outra_conversa()
    {
        var contatoAntigo = Agora.AddMonths(-13);
        var conversaAntiga = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo);
        conversaAntiga.RegistrarTurno("mensagem antiga", Resposta(), contatoAntigo);

        var contatoRecente = Agora.AddMonths(-2);
        var conversaRecente = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoRecente);
        conversaRecente.ReapontarLead(conversaAntiga.Lead);
        conversaRecente.RegistrarTurno("mensagem recente", Resposta(), contatoRecente);

        using var provedor = CriarProvedor();
        await SalvarConversasAsync(provedor, conversaAntiga, conversaRecente);
        var servico = CriarServico(provedor, CriarConfiguracao(), Agora);

        var expurgados = await servico.ExecutarCicloAsync();

        Assert.Equal(0, expurgados);
        Assert.Equal((1, 2, 4), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Ciclo_sem_mensagem_do_lead_usa_a_data_de_criacao_mais_antiga()
    {
        var origemDoLead = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddMonths(-13));
        var conversaAtual = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddDays(-2));
        conversaAtual.ReapontarLead(origemDoLead.Lead);

        using var provedor = CriarProvedor();
        await SalvarConversasAsync(provedor, conversaAtual);
        var servico = CriarServico(provedor, CriarConfiguracao(), Agora);

        var expurgados = await servico.ExecutarCicloAsync();

        Assert.Equal(1, expurgados);
        Assert.Equal((0, 0, 0), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Ciclo_retorna_contagem_e_cancelamento_preserva_os_registros()
    {
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddMonths(-13));
        conversa.RegistrarTurno("mensagem antiga", Resposta(), Agora.AddMonths(-13));

        using var provedor = CriarProvedor();
        await SalvarConversasAsync(provedor, conversa);
        var servico = CriarServico(provedor, CriarConfiguracao(), Agora);
        using var cancelamento = new CancellationTokenSource();
        cancelamento.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => servico.ExecutarCicloAsync(cancelamento.Token));

        Assert.Equal((1, 1, 2), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Prazo_usa_meses_de_calendario_como_AddMonths()
    {
        var agora = new DateTimeOffset(2025, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var limite = agora.AddMonths(-12);
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, limite);
        conversa.RegistrarTurno("mensagem no limite", Resposta(), limite);

        using var provedor = CriarProvedor();
        await SalvarConversasAsync(provedor, conversa);
        var servico = CriarServico(provedor, CriarConfiguracao(), agora);

        var expurgados = await servico.ExecutarCicloAsync();

        Assert.Equal(1, expurgados);
        Assert.Equal((0, 0, 0), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task BackgroundService_executa_primeiro_ciclo_apos_atraso_sem_aguardar_intervalo()
    {
        using var provedor = CriarProvedor();
        var contatoAntigo = Agora.AddMonths(-13);
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo);
        await SalvarConversasAsync(provedor, conversa);

        var logs = new SinalizadorDeLogs();
        using var servico = CriarServico(
            provedor,
            CriarConfiguracao(
                atrasoInicial: TimeSpan.FromMilliseconds(25),
                intervaloVarredura: TimeSpan.FromDays(1)),
            Agora,
            logs);

        await servico.StartAsync(CancellationToken.None);
        try
        {
            var auditoria = await logs.AuditoriaDeSucesso.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Expurgo por retencao:", auditoria);
        }
        finally
        {
            using var prazoDeParada = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await servico.StopAsync(prazoDeParada.Token);
        }

        Assert.Equal(0, logs.QuantidadeErros);
        Assert.Equal((0, 0, 0), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Cancelamento_durante_atraso_inicial_nao_executa_ciclo_nem_registra_erro()
    {
        using var provedor = CriarProvedor();
        var contatoAntigo = Agora.AddMonths(-13);
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo);
        await SalvarConversasAsync(provedor, conversa);

        var logs = new SinalizadorDeLogs();
        using var servico = CriarServico(
            provedor,
            CriarConfiguracao(
                atrasoInicial: TimeSpan.FromHours(1),
                intervaloVarredura: TimeSpan.FromDays(1)),
            Agora,
            logs);

        await servico.StartAsync(CancellationToken.None);
        using var prazoDeParada = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await servico.StopAsync(prazoDeParada.Token);

        Assert.False(logs.AuditoriaDeSucesso.IsCompleted);
        Assert.Equal(0, logs.QuantidadeErros);
        Assert.Equal((1, 1, 0), await ObterContagensAsync(provedor));
    }

    [Fact]
    public async Task Falha_no_primeiro_ciclo_nao_impede_execucoes_agendadas_posteriores()
    {
        using var provedor = CriarProvedor();
        var contatoAntigo = Agora.AddMonths(-13);
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo);
        await SalvarConversasAsync(provedor, conversa);

        var logs = new SinalizadorDeLogs();
        var scopeFactory = new FalhaNoPrimeiroEscopo(
            provedor.GetRequiredService<IServiceScopeFactory>());
        using var servico = CriarServico(
            provedor,
            CriarConfiguracao(
                atrasoInicial: TimeSpan.FromMilliseconds(25),
                intervaloVarredura: TimeSpan.FromMilliseconds(25)),
            Agora,
            logs,
            scopeFactory);

        await servico.StartAsync(CancellationToken.None);
        try
        {
            await logs.AuditoriaDeSucesso.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var prazoDeParada = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await servico.StopAsync(prazoDeParada.Token);
        }

        Assert.True(logs.QuantidadeErros >= 1);
        Assert.Equal((0, 0, 0), await ObterContagensAsync(provedor));
    }

    private static IConfiguration CriarConfiguracao(
        int meses = 12,
        TimeSpan? atrasoInicial = null,
        TimeSpan? intervaloVarredura = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Expurgo:PrazoRetencaoMeses"] = meses.ToString(),
            ["Expurgo:AtrasoInicial"] = atrasoInicial?.ToString("c"),
            ["Expurgo:IntervaloVarredura"] = intervaloVarredura?.ToString("c"),
        })
        .Build();

    private static ServicoDeExpurgo CriarServico(
        ServiceProvider provedor,
        IConfiguration configuracao,
        DateTimeOffset agora,
        ILogger<ServicoDeExpurgo>? logger = null,
        IServiceScopeFactory? scopeFactory = null) => new(
        scopeFactory ?? provedor.GetRequiredService<IServiceScopeFactory>(),
        new TravaDeConversas(),
        configuracao,
        logger ?? NullLogger<ServicoDeExpurgo>.Instance,
        new RelogioFixo(agora));
    private static ServiceProvider CriarProvedor()
    {
        var nomeBanco = Guid.NewGuid().ToString();
        var raizBanco = new InMemoryDatabaseRoot();
        var servicos = new ServiceCollection();
        servicos.AddDbContext<SolarDbContext>(opcoes => opcoes
            .UseInMemoryDatabase(nomeBanco, raizBanco)
            .ConfigureWarnings(avisos => avisos.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        servicos.AddScoped<ConversaRepositorio>();

        return servicos.BuildServiceProvider();
    }

    private static async Task SalvarConversasAsync(ServiceProvider provedor, params Conversa[] conversas)
    {
        await using var escopo = provedor.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        db.Conversas.AddRange(conversas);
        await db.SaveChangesAsync();
    }

    private static async Task<(int Leads, int Conversas, int Mensagens)> ObterContagensAsync(ServiceProvider provedor)
    {
        await using var escopo = provedor.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();

        return (
            await db.Leads.CountAsync(),
            await db.Conversas.CountAsync(),
            await db.Mensagens.CountAsync());
    }

    private static TurnoResponse Resposta() => new(
        "Resposta de teste",
        Intencoes.Compra,
        new CamposExtraidos(),
        ProximasAcoes.ContinuarConversa,
        [],
        null);

    private static string LocalizarRaizDoRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "Solar.sln")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? throw new DirectoryNotFoundException("Raiz do repositorio nao encontrada.");
    }

    private sealed class FalhaNoPrimeiroEscopo(IServiceScopeFactory provedor) : IServiceScopeFactory
    {
        private int _falharProximaCriacao = 1;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Exchange(ref _falharProximaCriacao, 0) == 1)
            {
                throw new InvalidOperationException("Falha transitória de teste.");
            }

            return provedor.CreateScope();
        }
    }

    private sealed class SinalizadorDeLogs : ILogger<ServicoDeExpurgo>
    {
        private readonly TaskCompletionSource<string> _auditoriaDeSucesso =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _quantidadeErros;

        public Task<string> AuditoriaDeSucesso => _auditoriaDeSucesso.Task;
        public int QuantidadeErros => Volatile.Read(ref _quantidadeErros);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Interlocked.Increment(ref _quantidadeErros);
            }

            var mensagem = formatter(state, exception);
            if (logLevel == LogLevel.Information &&
                mensagem.StartsWith("Expurgo por retencao:", StringComparison.Ordinal))
            {
                _auditoriaDeSucesso.TrySetResult(mensagem);
            }
        }
    }
    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }
}
