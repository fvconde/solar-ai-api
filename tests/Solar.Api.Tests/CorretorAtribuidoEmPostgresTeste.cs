using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Agendamentos;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class CorretorAtribuidoEmPostgresTeste : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 10, 5, 10, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 10, 5, 10, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 10, 5, 10, 15, 0, TimeSpan.Zero);

    private readonly string _schema = $"s45_corretor_{Guid.NewGuid():N}";
    private string _conexaoBase = string.Empty;

    public async Task InitializeAsync()
    {
        _conexaoBase = PostgresTestDatabase.ObterConexaoParaAplicacao();

        await using var admin = new NpgsqlConnection(_conexaoBase);
        await admin.OpenAsync();
        await using (var criarSchema = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", admin))
        {
            await criarSchema.ExecuteNonQueryAsync();
        }

        await using var db = CriarContexto();
        await db.Database.MigrateAsync();

        var corretores = await db.Corretores.ToListAsync();
        foreach (var c in corretores)
        {
            c.Desativar();
        }
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_conexaoBase))
        {
            return;
        }

        await using var admin = new NpgsqlConnection(_conexaoBase);
        await admin.OpenAsync();
        await using var removerSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", admin);
        await removerSchema.ExecuteNonQueryAsync();
    }

    private SolarDbContext CriarContexto()
    {
        var builder = new NpgsqlConnectionStringBuilder(_conexaoBase)
        {
            SearchPath = _schema,
        };

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(builder.ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", _schema))
            .Options;

        return new SolarDbContext(options);
    }

    private static Corretor CriarCorretor(string nome, string regiao, DateTimeOffset em)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var corretor = Corretor.NovaConta(
            nome: nome,
            email: $"{id}@solar.test",
            emailNormalizado: $"{id}@SOLAR.TEST",
            telefone: "11999999999",
            senhaHash: "hash_teste",
            perfil: PerfisDoPainel.Corretor,
            regioes: [regiao],
            especialidades: [Especialidades.Moradia],
            versaoAvisoPrivacidade: "1.0",
            em: em);
        corretor.Aprovar(em);
        return corretor;
    }

    [Fact]
    public async Task DecidirAsync_com_candidato_grava_corretor_atribuido_em_e_segunda_decisao_retorna_novo_nulo_preservando_marco()
    {
        await using var db = CriarContexto();
        var repoConversa = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);
        var repoEncaminhamento = new EncaminhamentoRepositorio(db);

        var regiao = $"regiao{Guid.NewGuid():N}"[..12];
        var corretorA = CriarCorretor("Corretor A", regiao, T0);
        db.Corretores.Add(corretorA);
        await db.SaveChangesAsync();

        var conversaId = Guid.NewGuid();
        var conversa = await repoConversa.ObterOuCriarAsync(conversaId, T0, default);
        conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: regiao), T0);
        await db.SaveChangesAsync();

        var turnoT1 = new TurnoResponse(
            Resposta: "Agendando reuniao.",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(Regiao: regiao),
            ProximaAcao: ProximasAcoes.AgendarReuniao,
            ImoveisSugeridos: [],
            SlotEscolhido: null,
            EssenciaisCompletos: true);

        repoConversa.AplicarTurno(conversa, "Quero agendar", turnoT1, T1);
        var handoff1 = await repoEncaminhamento.DecidirAsync(conversa, ProximasAcoes.AgendarReuniao, T1, default);

        Assert.NotNull(handoff1.Novo);
        Assert.Equal(corretorA.Id, handoff1.Novo.CorretorId);

        await gravacao.SalvarAsync(conversa, handoff1.Novo, [], null, T1, default);

        db.ChangeTracker.Clear();

        var recarregada1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, recarregada1.CorretorAtribuidoEm);

        var conversaParaT2 = await repoConversa.ObterAsync(conversaId, default);
        Assert.NotNull(conversaParaT2);

        var handoff2 = await repoEncaminhamento.DecidirAsync(conversaParaT2, ProximasAcoes.AgendarReuniao, T2, default);
        Assert.Null(handoff2.Novo);

        db.ChangeTracker.Clear();

        var recarregada2 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, recarregada2.CorretorAtribuidoEm);

        var encaminhamentos = await db.Encaminhamentos.AsNoTracking().Where(e => e.ConversaId == conversaId).ToListAsync();
        var unico = Assert.Single(encaminhamentos);
        Assert.Equal(corretorA.Id, unico.CorretorId);
    }

    [Fact]
    public async Task Sem_candidatos_elegiveis_produz_encaminhamento_aguardando_sem_marco_corretor_com_encaminhada_em_presente()
    {
        await using var db = CriarContexto();
        var repoConversa = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);
        var repoEncaminhamento = new EncaminhamentoRepositorio(db);

        var regiao = $"regiao{Guid.NewGuid():N}"[..12];
        var conversaId = Guid.NewGuid();
        var conversa = await repoConversa.ObterOuCriarAsync(conversaId, T0, default);
        conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: regiao), T0);
        await db.SaveChangesAsync();

        var turno = new TurnoResponse(
            Resposta: "Direcionando para especialista.",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(Regiao: regiao),
            ProximaAcao: ProximasAcoes.DirecionarEspecialista,
            ImoveisSugeridos: [],
            SlotEscolhido: null,
            EssenciaisCompletos: false);

        repoConversa.AplicarTurno(conversa, "Quero especialista", turno, T1);
        var handoff = await repoEncaminhamento.DecidirAsync(conversa, ProximasAcoes.DirecionarEspecialista, T1, default);

        Assert.NotNull(handoff.Novo);
        Assert.Null(handoff.Novo.CorretorId);
        Assert.Equal(StatusDoEncaminhamento.Aguardando, handoff.Novo.Status);

        await gravacao.SalvarAsync(conversa, handoff.Novo, [], null, T1, default);

        db.ChangeTracker.Clear();

        var recarregada = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, recarregada.EncaminhadaEm);
        Assert.Null(recarregada.CorretorAtribuidoEm);

        var encaminhamentos = await db.Encaminhamentos.AsNoTracking().Where(e => e.ConversaId == conversaId).ToListAsync();
        var unico = Assert.Single(encaminhamentos);
        Assert.Null(unico.CorretorId);
        Assert.Equal(StatusDoEncaminhamento.Aguardando, unico.Status);
    }

    [Fact]
    public async Task RedistribuirAsync_grava_primeiro_marco_e_preserva_em_redistribuicoes_posteriores_mesmo_sem_substituto()
    {
        await using var db = CriarContexto();
        var repoEncaminhamento = new EncaminhamentoRepositorio(db);

        var regiao = $"regiao{Guid.NewGuid():N}"[..12];
        var corretorA = CriarCorretor("Corretor A", regiao, T0);
        db.Corretores.Add(corretorA);

        var conversaId = Guid.NewGuid();
        var conversa = Conversa.Nova(conversaId, Canais.Web, T0);
        conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: regiao), T0);
        db.Conversas.Add(conversa);

        var enc = Encaminhamento.Novo(conversa.Id, conversa.LeadId, corretorA.Id, Especialidades.Moradia, T0);
        db.Encaminhamentos.Add(enc);
        await db.SaveChangesAsync();

        var c0 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Null(c0.CorretorAtribuidoEm);

        corretorA.Desativar();
        var corretorB = CriarCorretor("Corretor B", regiao, T0);
        db.Corretores.Add(corretorB);
        await db.SaveChangesAsync();

        await repoEncaminhamento.RedistribuirAsync(corretorA.Id, T1, default);

        db.ChangeTracker.Clear();

        var c1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, c1.CorretorAtribuidoEm);
        var e1 = await db.Encaminhamentos.AsNoTracking().SingleAsync(e => e.ConversaId == conversaId);
        Assert.Equal(corretorB.Id, e1.CorretorId);

        var corretorBEntidade = await db.Corretores.SingleAsync(c => c.Id == corretorB.Id);
        corretorBEntidade.Desativar();
        var corretorC = CriarCorretor("Corretor C", regiao, T0);
        db.Corretores.Add(corretorC);
        await db.SaveChangesAsync();

        await repoEncaminhamento.RedistribuirAsync(corretorB.Id, T2, default);

        db.ChangeTracker.Clear();

        var c2 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, c2.CorretorAtribuidoEm);
        var e2 = await db.Encaminhamentos.AsNoTracking().SingleAsync(e => e.ConversaId == conversaId);
        Assert.Equal(corretorC.Id, e2.CorretorId);

        var corretorCEntidade = await db.Corretores.SingleAsync(c => c.Id == corretorC.Id);
        corretorCEntidade.Desativar();
        await db.SaveChangesAsync();

        await repoEncaminhamento.RedistribuirAsync(corretorC.Id, T3, default);

        db.ChangeTracker.Clear();

        var c3 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Equal(T1, c3.CorretorAtribuidoEm);
        var e3 = await db.Encaminhamentos.AsNoTracking().SingleAsync(e => e.ConversaId == conversaId);
        Assert.Null(e3.CorretorId);
        Assert.Equal(StatusDoEncaminhamento.Aguardando, e3.Status);
    }

    [Fact]
    public async Task Linha_legada_sem_marco_redistribuida_sem_substituto_mantem_marco_nulo_e_atribuicao_nula()
    {
        await using var db = CriarContexto();
        var repoEncaminhamento = new EncaminhamentoRepositorio(db);

        var regiao = $"regiao{Guid.NewGuid():N}"[..12];
        var corretorA = CriarCorretor("Corretor A", regiao, T0);
        db.Corretores.Add(corretorA);

        var conversaId = Guid.NewGuid();
        var conversa = Conversa.Nova(conversaId, Canais.Web, T0);
        conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(Regiao: regiao), T0);
        db.Conversas.Add(conversa);

        var enc = Encaminhamento.Novo(conversa.Id, conversa.LeadId, corretorA.Id, Especialidades.Moradia, T0);
        db.Encaminhamentos.Add(enc);
        await db.SaveChangesAsync();

        corretorA.Desativar();
        await db.SaveChangesAsync();

        await repoEncaminhamento.RedistribuirAsync(corretorA.Id, T1, default);

        db.ChangeTracker.Clear();

        var c1 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Null(c1.CorretorAtribuidoEm);
        var e1 = await db.Encaminhamentos.AsNoTracking().SingleAsync(e => e.ConversaId == conversaId);
        Assert.Null(e1.CorretorId);
        Assert.Equal(StatusDoEncaminhamento.Aguardando, e1.Status);
    }

    [Fact]
    public async Task Turno_com_falha_de_chave_estrangeira_em_encaminhamento_faz_rollback_e_mantem_marco_e_mensagens_nulos()
    {
        await using var db = CriarContexto();
        var repoConversa = new ConversaRepositorio(db);
        var agenda = new AgendaRepositorio(db);
        var gravacao = new GravacaoDoTurno(db, agenda);

        var conversaId = Guid.NewGuid();
        var conversa = await repoConversa.ObterOuCriarAsync(conversaId, T0, default);
        conversa.Lead.Fundir(Intencoes.Compra, new CamposExtraidos(), T0);
        await db.SaveChangesAsync();

        var c0 = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Null(c0.CorretorAtribuidoEm);

        var turno = new TurnoResponse(
            Resposta: "Agendando reuniao.",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.AgendarReuniao,
            ImoveisSugeridos: [],
            SlotEscolhido: null,
            EssenciaisCompletos: true);

        repoConversa.AplicarTurno(conversa, "Quero agendar", turno, T1);
        conversa.RegistrarCorretorAtribuido(T1);
        Assert.Equal(T1, conversa.CorretorAtribuidoEm);

        var corretorInexistenteId = Guid.NewGuid();
        var encaminhamentoInvalido = Encaminhamento.Novo(
            conversa.Id, conversa.LeadId, corretorInexistenteId, Especialidades.Moradia, T1);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            gravacao.SalvarAsync(conversa, encaminhamentoInvalido, [], null, T1, default));

        db.ChangeTracker.Clear();

        var recarregada = await db.Conversas.AsNoTracking().SingleAsync(c => c.Id == conversaId);
        Assert.Null(recarregada.CorretorAtribuidoEm);

        var totalEncaminhamentos = await db.Encaminhamentos.AsNoTracking()
            .Where(e => e.ConversaId == conversaId)
            .CountAsync();
        Assert.Equal(0, totalEncaminhamentos);

        var totalMensagens = await db.Mensagens.AsNoTracking()
            .Where(m => m.ConversaId == conversaId)
            .CountAsync();
        Assert.Equal(0, totalMensagens);
    }
}
