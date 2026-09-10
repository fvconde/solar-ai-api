using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Encaminhamentos;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public class ExclusaoLeadPostgresTeste
{
    private const string NomeBancoTestesEsperado = "solar_test";

    private static string ObterConexaoPostgresTest()
    {
        // 1. Variavel de ambiente especifica para banco de testes
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__PostgresTest");
        if (!string.IsNullOrWhiteSpace(cs))
        {
            ValidarBancoDedicado(cs);
            return cs;
        }

        // 2. Le do arquivo .env local (gitignored) para montar a conexao com o banco dedicado
        var envPath = LocalizarArquivoEnv();
        if (envPath != null)
        {
            var linhas = File.ReadAllLines(envPath);
            var user = linhas.FirstOrDefault(l => l.StartsWith("POSTGRES_USER="))?.Split('=', 2)[1].Trim() ?? "solar";
            var pass = linhas.FirstOrDefault(l => l.StartsWith("POSTGRES_PASSWORD="))?.Split('=', 2)[1].Trim();

            if (!string.IsNullOrWhiteSpace(pass))
            {
                var montada = $"Host=localhost;Port=5432;Database={NomeBancoTestesEsperado};Username={user};Password={pass}";
                ValidarBancoDedicado(montada);
                return montada;
            }
        }

        throw new InvalidOperationException(
            "Configuracao para o banco de testes dedicado nao encontrada. " +
            "Defina ConnectionStrings__PostgresTest ou certifique-se de que o arquivo .env contem POSTGRES_PASSWORD.");
    }

    private static void ValidarBancoDedicado(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.Equals(builder.Database, "solar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SEGURANCA: Os testes de integracao nao podem usar o banco de desenvolvimento 'solar'. " +
                $"Utilize exclusivamente um banco descartavel dedicado (ex: '{NomeBancoTestesEsperado}').");
        }
    }

    private static string? LocalizarArquivoEnv()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            if (string.IsNullOrEmpty(dir)) break;

            var cand1 = Path.Combine(dir, ".env");
            if (File.Exists(cand1)) return cand1;

            var cand2 = Path.Combine(dir, "solar-ai-api", ".env");
            if (File.Exists(cand2)) return cand2;

            var pai = Directory.GetParent(dir);
            if (pai == null) break;
            dir = pai.FullName;
        }
        return null;
    }

    private static async Task<SolarDbContext> CriarContextoPostgresTestAsync()
    {
        string conexao;
        try
        {
            conexao = ObterConexaoPostgresTest();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Falha na resolucao da connection string de testes: {ex.Message}");
            throw;
        }

        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;

        var db = new SolarDbContext(options);

        // Nao permite passar silenciosamente/vacuamente se o banco estiver inacessivel:
        // A integracao e obrigatoria e precisa executar e passar.
        var podeConectar = false;
        try
        {
            podeConectar = await db.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail($"Falha ao conectar ao banco de testes dedicado '{NomeBancoTestesEsperado}': {ex.Message}");
        }

        if (!podeConectar)
        {
            Assert.Fail($"Nao foi possivel conectar ao banco de testes dedicado '{NomeBancoTestesEsperado}'. " +
                        "A validacao obrigatoria de integracao precisa executar e passar.");
        }

        // Garante que o banco de testes dedicado possui o schema atualizado com as migrations
        await db.Database.MigrateAsync();

        return db;
    }

    [Fact]
    public async Task Postgres_Exclusao_do_lead_elimina_todas_tabelas_em_cascata_e_preserva_outro_lead()
    {
        using var db = await CriarContextoPostgresTestAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        // 1. Criar Lead Alvo (Lead A) com 2 conversas, mensagens e encaminhamento
        var conversaA1Id = Guid.NewGuid();
        var conversaA2Id = Guid.NewGuid();

        var conversaA1 = await repo.ObterOuCriarAsync(conversaA1Id, agora, default);
        var leadA = conversaA1.Lead;
        leadA.RegistrarContato("Lead A LGPD", "11911112222", "leada.lgpd@teste.local", agora);

        var conversaA2 = Conversa.Nova(conversaA2Id, Canais.Web, agora);
        conversaA2.ReapontarLead(leadA);
        db.Conversas.Add(conversaA2);

        var turno = new TurnoResponse(
            Resposta: "Imóveis recomendados para compra.",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(Regiao: "Moema", PrecoMax: 1200000),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [],
            SlotEscolhido: null);

        repo.AplicarTurno(conversaA1, "Busco apartamento em Moema", turno, agora);
        repo.AplicarTurno(conversaA2, "Alguma cobertura disponível?", turno, agora);

        var encaminhamentoA = Encaminhamento.Novo(conversaA1Id, leadA.Id, null, Especialidades.Moradia, agora);
        db.Encaminhamentos.Add(encaminhamentoA);

        // 2. Criar Lead B (independente) para verificar isolamento e preservação
        var conversaBId = Guid.NewGuid();
        var conversaB = await repo.ObterOuCriarAsync(conversaBId, agora, default);
        var leadB = conversaB.Lead;
        leadB.RegistrarContato("Lead B Preservado", "11933334444", "leadb@teste.local", agora);
        repo.AplicarTurno(conversaB, "Aluguel na Paulista", turno, agora);

        await db.SaveChangesAsync();

        var leadAId = leadA.Id;
        var leadBId = leadB.Id;

        // Verificar que registros do Lead A foram persistidos no Postgres
        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadAId));
        Assert.Equal(2, await db.Conversas.CountAsync(c => c.LeadId == leadAId));
        Assert.Equal(4, await db.Mensagens.CountAsync(m => m.ConversaId == conversaA1Id || m.ConversaId == conversaA2Id));
        Assert.Equal(1, await db.Encaminhamentos.CountAsync(e => e.LeadId == leadAId));

        // 3. Executar exclusão do Lead A (LGPD)
        var resultado = await repo.ExcluirLeadAsync(leadAId, default);
        Assert.NotNull(resultado);
        Assert.Equal(leadAId, resultado.LeadId);
        Assert.Equal(2, resultado.ConversasAfetadas);
        Assert.Equal(4, resultado.MensagensExcluidas);

        // 4. Validação direta nas tabelas do Postgres: todos os registros do Lead A foram apagados
        Assert.False(await db.Leads.AnyAsync(l => l.Id == leadAId));
        Assert.Equal(0, await db.Conversas.CountAsync(c => c.LeadId == leadAId));
        Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversaA1Id || m.ConversaId == conversaA2Id));
        Assert.Equal(0, await db.Encaminhamentos.CountAsync(e => e.LeadId == leadAId));

        // 5. Validação de isolamento: Lead B e seus registros permanecem 100% intactos no Postgres
        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadBId));
        Assert.Equal(1, await db.Conversas.CountAsync(c => c.LeadId == leadBId));
        Assert.Equal(2, await db.Mensagens.CountAsync(m => m.ConversaId == conversaBId));

        // Limpeza do Lead B de teste
        await repo.ExcluirLeadAsync(leadBId, default);
    }

    [Fact]
    public async Task Postgres_Exclusao_apenas_conversa_preserva_lead_e_outra_conversa()
    {
        using var db = await CriarContextoPostgresTestAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();

        var conversa1 = await repo.ObterOuCriarAsync(conversa1Id, agora, default);
        var lead = conversa1.Lead;
        lead.RegistrarContato("Cliente Multiconversa", "11955556666", "multi@teste.local", agora);

        var conversa2 = Conversa.Nova(conversa2Id, Canais.Web, agora);
        conversa2.ReapontarLead(lead);
        db.Conversas.Add(conversa2);

        var turno = new TurnoResponse(
            Resposta: "Informações sobre o imóvel",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [],
            SlotEscolhido: null);

        repo.AplicarTurno(conversa1, "Mensagem C1", turno, agora);
        repo.AplicarTurno(conversa2, "Mensagem C2", turno, agora);

        await db.SaveChangesAsync();

        var leadId = lead.Id;

        // Exclui apenas a conversa 1
        var resultado = await repo.ExcluirApenasConversaAsync(conversa1Id, default);
        Assert.NotNull(resultado);
        Assert.Equal(conversa1Id, resultado.ConversaId);
        Assert.Equal(leadId, resultado.LeadId);
        Assert.False(resultado.LeadExcluido);
        Assert.Equal(2, resultado.MensagensExcluidas);

        // Conversa 1 e suas mensagens sumiram do Postgres
        Assert.False(await db.Conversas.AnyAsync(c => c.Id == conversa1Id));
        Assert.Equal(0, await db.Mensagens.CountAsync(m => m.ConversaId == conversa1Id));

        // Lead e Conversa 2 continuam existindo no Postgres
        Assert.True(await db.Leads.AnyAsync(l => l.Id == leadId));
        Assert.True(await db.Conversas.AnyAsync(c => c.Id == conversa2Id));
        Assert.Equal(2, await db.Mensagens.CountAsync(m => m.ConversaId == conversa2Id));

        // Limpeza
        await repo.ExcluirLeadAsync(leadId, default);
    }

    [Fact]
    public async Task Postgres_Concorrencia_Exclusao_coordena_travas_de_todas_conversas_do_lead()
    {
        using var db = await CriarContextoPostgresTestAsync();
        var repo = new ConversaRepositorio(db);
        var travas = new TravaDeConversas();
        var agora = DateTimeOffset.UtcNow;

        var c1Id = Guid.NewGuid();
        var c2Id = Guid.NewGuid();

        var c1 = await repo.ObterOuCriarAsync(c1Id, agora, default);
        var lead = c1.Lead;

        var c2 = Conversa.Nova(c2Id, Canais.Web, agora);
        c2.ReapontarLead(lead);
        db.Conversas.Add(c2);
        await db.SaveChangesAsync();

        var leadId = lead.Id;
        var conversaIds = await repo.ObterIdsDeConversasDoLeadAsync(leadId, default);
        Assert.Contains(c1Id, conversaIds);
        Assert.Contains(c2Id, conversaIds);

        var idsParaTravar = conversaIds.Append(leadId);

        var lockExclusaoAtivo = false;
        var c1EntrouAntesDaLiberacao = false;
        var c2EntrouAntesDaLiberacao = false;
        var c1Executou = false;
        var c2Executou = false;

        Task taskMensagemC1;
        Task taskMensagemC2;

        using (await travas.TravarMultiplasAsync(idsParaTravar, default))
        {
            lockExclusaoAtivo = true;

            // Simula requisicoes concorrentes de mensagem chegando para C1 e C2
            taskMensagemC1 = Task.Run(async () =>
            {
                using var _ = await travas.TravarAsync(c1Id, default);
                if (lockExclusaoAtivo)
                {
                    c1EntrouAntesDaLiberacao = true;
                }
                c1Executou = true;
            });

            taskMensagemC2 = Task.Run(async () =>
            {
                using var _ = await travas.TravarAsync(c2Id, default);
                if (lockExclusaoAtivo)
                {
                    c2EntrouAntesDaLiberacao = true;
                }
                c2Executou = true;
            });

            await Task.Delay(100);
            Assert.False(c1EntrouAntesDaLiberacao, "Requisicao de C1 nao deve executar enquanto lock de exclusao estiver ativo");
            Assert.False(c2EntrouAntesDaLiberacao, "Requisicao de C2 nao deve executar enquanto lock de exclusao estiver ativo");
            Assert.False(c1Executou, "Task C1 deve estar aguardando liberacao da trava");
            Assert.False(c2Executou, "Task C2 deve estar aguardando liberacao da trava");

            // Executa a exclusao no banco enquanto as travas estao seguras
            await repo.ExcluirLeadAsync(leadId, default);
            lockExclusaoAtivo = false;
        }

        // Apos liberar o lock de exclusao, as tasks adquirem a trava e terminam
        await Task.WhenAll(taskMensagemC1, taskMensagemC2);
        Assert.True(c1Executou);
        Assert.True(c2Executou);

        // Confirma que lead foi removido no Postgres
        Assert.False(await db.Leads.AnyAsync(l => l.Id == leadId));
    }
}
