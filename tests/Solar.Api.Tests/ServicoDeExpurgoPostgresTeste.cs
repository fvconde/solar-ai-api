using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class ServicoDeExpurgoPostgresTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string NomeCanario = "S39_NOME_NAO_REGISTRAR";
    private const string TelefoneCanario = "11988776655";
    private const string EmailCanario = "s39-auditoria-nao-registrar@tests.solar.local";
    private const string TextoCanario = "S39_TEXTO_PRIVADO_NAO_REGISTRAR";
    private const string ExcecaoCanario = "S39_EXCECAO_COM_DADO_PRIVADO";

    [Fact]
    public async Task Expurgo_remove_lead_antigo_e_todos_os_dados_e_audita_apenas_contagem_e_horario()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);

        var ultimoContato = Agora.AddMonths(-13);
        var conversaA = NovaConversaComMensagem(ultimoContato, TextoCanario);
        conversaA.Lead.RegistrarContato(NomeCanario, TelefoneCanario, EmailCanario, ultimoContato);
        var conversaB = Conversa.Nova(Guid.NewGuid(), Canais.Web, ultimoContato.AddDays(1));
        conversaB.ReapontarLead(conversaA.Lead);
        RegistrarMensagemDoLead(conversaB, ultimoContato.AddDays(1));
        var leadId = conversaA.LeadId;
        var encaminhamento = Encaminhamento.Novo(
            conversaA.Id,
            leadId,
            null,
            Especialidades.Moradia,
            ultimoContato);

        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
            {
                db.Conversas.AddRange(conversaA, conversaB);
                db.Encaminhamentos.Add(encaminhamento);
            });

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(1, expurgados);
            Assert.Equal(new Contagens(0, 0, 0, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
            await AssertarAuditoriaDeSucesso(harness.Logs, 1);
            AssertarLogsSemDadosPessoais(harness.Logs, leadId, conversaA.Id, conversaB.Id);
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Lead_recente_permanece_no_Postgres()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoRecente = Agora.AddMonths(-2);
        var conversa = NovaConversaComMensagem(contatoRecente);
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db => db.Conversas.Add(conversa));

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(0, expurgados);
            Assert.Equal(new Contagens(1, 1, 2, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Mensagem_recente_do_lead_seguida_de_follow_up_nao_e_expurgada()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoRecente = Agora.AddMonths(-2);
        var conversa = NovaConversaComMensagem(contatoRecente);
        conversa.RegistrarFollowUp(Resposta(), Agora.AddDays(-1));
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db => db.Conversas.Add(conversa));

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(0, expurgados);
            Assert.Equal(new Contagens(1, 1, 3, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Mensagem_recente_em_outra_conversa_preserva_lead_com_conversa_antiga()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoAntigo = Agora.AddMonths(-13);
        var conversaAntiga = NovaConversaComMensagem(contatoAntigo);
        var contatoRecente = Agora.AddMonths(-2);
        var conversaRecente = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoRecente);
        conversaRecente.ReapontarLead(conversaAntiga.Lead);
        RegistrarMensagemDoLead(conversaRecente, contatoRecente);
        var leadId = conversaAntiga.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
                db.Conversas.AddRange(conversaAntiga, conversaRecente));

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(0, expurgados);
            Assert.Equal(new Contagens(1, 2, 4, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Expurgo_da_conversa_preserva_conta_cliente_e_sessao()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var conta = NovaConta(PerfisDoPainel.Cliente, Agora);
        var sessao = SessaoCorretor.Nova(
            conta.Id,
            SHA256.HashData(Guid.NewGuid().ToByteArray()),
            Agora);
        var conversa = NovaConversaComMensagem(Agora.AddMonths(-13));
        conversa.VincularConta(conta.Id);
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);
        limpeza.AdicionarConta(conta.Id);

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
            {
                db.Corretores.Add(conta);
                db.Sessoes.Add(sessao);
                db.Conversas.Add(conversa);
            });

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(1, expurgados);
            Assert.Equal(new Contagens(0, 0, 0, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
            await using var verificar = NovoContexto(harness.ConnectionString);
            Assert.True(await verificar.Corretores.AnyAsync(c =>
                c.Id == conta.Id && c.Perfil == PerfisDoPainel.Cliente));
            Assert.True(await verificar.Sessoes.AnyAsync(s =>
                s.Id == sessao.Id && s.CorretorId == conta.Id));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Follow_up_recente_de_agente_nao_estende_prazo_do_ultimo_contato_do_lead()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoAntigo = Agora.AddMonths(-13);
        var conversa = NovaConversaComMensagem(contatoAntigo);
        conversa.RegistrarFollowUp(Resposta(), Agora.AddDays(-1));
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db => db.Conversas.Add(conversa));

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(1, expurgados);
            Assert.Equal(new Contagens(0, 0, 0, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Lead_encaminhado_usa_o_mesmo_prazo_de_retencao()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var corretor = NovaConta(PerfisDoPainel.Corretor, Agora);
        corretor.Aprovar(Agora);

        var contatoAntigo = Agora.AddMonths(-13);
        var conversaAntiga = NovaConversaComMensagem(contatoAntigo);
        conversaAntiga.Lead.MarcarEncaminhado(contatoAntigo);
        var contatoRecente = Agora.AddMonths(-11);
        var conversaRecente = NovaConversaComMensagem(contatoRecente);
        conversaRecente.Lead.MarcarEncaminhado(contatoRecente);
        var encaminhamentoAntigo = Encaminhamento.Novo(
            conversaAntiga.Id,
            conversaAntiga.LeadId,
            corretor.Id,
            Especialidades.Moradia,
            contatoAntigo);
        var encaminhamentoRecente = Encaminhamento.Novo(
            conversaRecente.Id,
            conversaRecente.LeadId,
            corretor.Id,
            Especialidades.Moradia,
            contatoRecente);
        limpeza.AdicionarLead(conversaAntiga.LeadId);
        limpeza.AdicionarLead(conversaRecente.LeadId);
        limpeza.AdicionarConta(corretor.Id);

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
            {
                db.Corretores.Add(corretor);
                db.Conversas.AddRange(conversaAntiga, conversaRecente);
                db.Encaminhamentos.AddRange(encaminhamentoAntigo, encaminhamentoRecente);
            });

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(1, expurgados);
            Assert.Equal(
                new Contagens(0, 0, 0, 0),
                await ContarLeadAsync(harness.ConnectionString, conversaAntiga.LeadId));
            Assert.Equal(
                new Contagens(1, 1, 2, 1),
                await ContarLeadAsync(harness.ConnectionString, conversaRecente.LeadId));
            await using var verificar = NovoContexto(harness.ConnectionString);
            Assert.True(await verificar.Corretores.AnyAsync(c => c.Id == corretor.Id));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Fallback_sem_mensagem_do_lead_usa_a_data_mais_antiga_e_cobre_lead_sem_conversa()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);

        var leadCriadoAntes = Lead.Novo(Agora.AddMonths(-13));
        var conversaCriadaDepois = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddMonths(-1));
        conversaCriadaDepois.ReapontarLead(leadCriadoAntes);
        conversaCriadaDepois.RegistrarFollowUp(Resposta(), Agora.AddDays(-1));

        var leadRecente = Lead.Novo(Agora.AddMonths(-6));
        var conversaCriadaAntes = Conversa.Nova(Guid.NewGuid(), Canais.Web, Agora.AddMonths(-13));
        conversaCriadaAntes.ReapontarLead(leadRecente);

        var leadSemConversa = Lead.Novo(Agora.AddMonths(-13));
        limpeza.AdicionarLead(leadCriadoAntes.Id);
        limpeza.AdicionarLead(leadRecente.Id);
        limpeza.AdicionarLead(leadSemConversa.Id);

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
            {
                db.Conversas.AddRange(conversaCriadaDepois, conversaCriadaAntes);
                db.Leads.Add(leadSemConversa);
            });

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(3, expurgados);
            Assert.Equal(
                new Contagens(0, 0, 0, 0),
                await ContarLeadAsync(harness.ConnectionString, leadCriadoAntes.Id));
            Assert.Equal(
                new Contagens(0, 0, 0, 0),
                await ContarLeadAsync(harness.ConnectionString, leadRecente.Id));
            Assert.Equal(
                new Contagens(0, 0, 0, 0),
                await ContarLeadAsync(harness.ConnectionString, leadSemConversa.Id));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Falha_no_meio_da_exclusao_faz_rollback_total_e_nao_registra_excecao_com_dados()
    {
        using var harness = await CriarHarnessAsync();
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoAntigo = Agora.AddMonths(-13);
        var conversaA = NovaConversaComMensagem(contatoAntigo, TextoCanario);
        conversaA.Lead.RegistrarContato(NomeCanario, TelefoneCanario, EmailCanario, contatoAntigo);
        var conversaB = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoAntigo.AddDays(1));
        conversaB.ReapontarLead(conversaA.Lead);
        RegistrarMensagemDoLead(conversaB, contatoAntigo.AddDays(1));
        var leadId = conversaA.LeadId;
        limpeza.AdicionarLead(leadId);
        var sufixo = Guid.NewGuid().ToString("N");
        var nomeFuncao = $"s39_falha_exclusao_{sufixo}";
        var nomeTrigger = $"s39_trigger_falha_{sufixo}";
        var nomeSequencia = $"s39_seq_falha_exclusao_{sufixo}";

        try
        {
            await SalvarAsync(harness.ConnectionString, db =>
            {
                db.Conversas.AddRange(conversaA, conversaB);
                db.Encaminhamentos.Add(Encaminhamento.Novo(
                    conversaA.Id,
                    leadId,
                    null,
                    Especialidades.Moradia,
                    contatoAntigo));
            });

            Assert.Equal(
                new Contagens(1, 2, 4, 1),
                await ContarLeadAsync(harness.ConnectionString, leadId));

            await ExecutarSqlAsync(
                harness.ConnectionString,
                $"CREATE SEQUENCE {nomeSequencia} START WITH 1;");

            await ExecutarSqlAsync(
                harness.ConnectionString,
                $@"CREATE FUNCTION {nomeFuncao}() RETURNS trigger
                    LANGUAGE plpgsql AS $s39$
                    BEGIN
                        IF OLD.id = TG_ARGV[0]::uuid
                           AND NOT EXISTS (SELECT 1 FROM conversas WHERE lead_id = OLD.id)
                           AND NOT EXISTS (SELECT 1 FROM encaminhamentos WHERE lead_id = OLD.id)
                           AND NOT EXISTS (
                               SELECT 1
                               FROM mensagens AS m
                               WHERE m.conversa_id IN (TG_ARGV[1]::uuid, TG_ARGV[2]::uuid)
                           )
                        THEN
                            PERFORM nextval('{nomeSequencia}');
                            RAISE EXCEPTION '{ExcecaoCanario}';
                        END IF;
                        RETURN OLD;
                    END;
                    $s39$;");

            await ExecutarSqlAsync(
                harness.ConnectionString,
                $"CREATE TRIGGER {nomeTrigger} BEFORE DELETE ON leads FOR EACH ROW WHEN (OLD.id = '{leadId:D}'::uuid) EXECUTE FUNCTION {nomeFuncao}('{leadId:D}', '{conversaA.Id:D}', '{conversaB.Id:D}');");

            var expurgados = await harness.Servico.ExecutarCicloAsync();

            Assert.Equal(0, expurgados);
            // nextval não volta com o rollback e só é chamado após as dependências sumirem.
            Assert.True(await SequenciaFoiUsadaAsync(harness.ConnectionString, nomeSequencia));
            Assert.Equal(
                new Contagens(1, 2, 4, 1),
                await ContarLeadAsync(harness.ConnectionString, leadId));
            Assert.Contains(harness.Logs.Registros, registro => registro.Nivel == LogLevel.Error);
            Assert.DoesNotContain(
                harness.Logs.Registros,
                registro => registro.Nivel == LogLevel.Information &&
                    registro.Mensagem.Contains("Expurgo por retencao", StringComparison.Ordinal));
            AssertarLogsSemDadosPessoais(harness.Logs, leadId, conversaA.Id, conversaB.Id);
        }
        finally
        {
            try
            {
                await ExecutarSqlAsync(
                    harness.ConnectionString,
                    $"DROP TRIGGER IF EXISTS {nomeTrigger} ON leads;");
            }
            finally
            {
                try
                {
                    await ExecutarSqlAsync(
                        harness.ConnectionString,
                        $"DROP FUNCTION IF EXISTS {nomeFuncao}();");
                }
                finally
                {
                    try
                    {
                        await ExecutarSqlAsync(
                            harness.ConnectionString,
                            $"DROP SEQUENCE IF EXISTS {nomeSequencia};");
                    }
                    finally
                    {
                        await limpeza.LimparAsync();
                    }
                }
            }
        }

        Assert.False(await ExisteObjetoDoCatalogoAsync(
            harness.ConnectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = @p0 AND NOT tgisinternal)",
            nomeTrigger));
        Assert.False(await ExisteObjetoDoCatalogoAsync(
            harness.ConnectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = @p0)",
            nomeFuncao));
        Assert.False(await ExisteObjetoDoCatalogoAsync(
            harness.ConnectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname = @p0 AND relkind = 'S')",
            nomeSequencia));
    }

    [Fact]
    public async Task Ciclo_revalida_apos_a_trava_se_chegar_nova_mensagem_do_lead()
    {
        var interceptador = new InterceptadorDaConsultaDeUltimaMensagem();
        using var harness = await CriarHarnessAsync(interceptador);
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var contatoAntigo = Agora.AddMonths(-13);
        var conversa = NovaConversaComMensagem(contatoAntigo);
        var conversaId = conversa.Id;
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db => db.Conversas.Add(conversa));

            var travaDaConversa = await harness.Travas.TravarAsync(conversaId, CancellationToken.None);
            using var cancelamento = new CancellationTokenSource();
            var ciclo = harness.Servico.ExecutarCicloAsync(cancelamento.Token);
            try
            {
                await interceptador.AguardarConsultaAsync();
                await RegistrarNovaMensagemDoLeadAsync(harness.ConnectionString, conversaId, Agora.AddDays(-1));
            }
            catch
            {
                cancelamento.Cancel();
                travaDaConversa.Dispose();
                try
                {
                    await ciclo.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                }

                throw;
            }
            finally
            {
                travaDaConversa.Dispose();
            }

            var expurgados = await ciclo.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(0, expurgados);
            Assert.Equal(new Contagens(1, 1, 4, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    [Fact]
    public async Task Cancelamento_enquanto_ciclo_aguarda_trava_preserva_todos_os_registros()
    {
        var interceptador = new InterceptadorDaConsultaDeUltimaMensagem();
        using var harness = await CriarHarnessAsync(interceptador);
        var limpeza = new LimpezaDoTeste(harness.ConnectionString);
        var conversa = NovaConversaComMensagem(Agora.AddMonths(-13));
        var conversaId = conversa.Id;
        var leadId = conversa.LeadId;
        limpeza.AdicionarLead(leadId);

        try
        {
            await SalvarAsync(harness.ConnectionString, db => db.Conversas.Add(conversa));

            var travaDaConversa = await harness.Travas.TravarAsync(conversaId, CancellationToken.None);
            using var cancelamento = new CancellationTokenSource();
            var ciclo = harness.Servico.ExecutarCicloAsync(cancelamento.Token);
            try
            {
                await interceptador.AguardarConsultaAsync();
                cancelamento.Cancel();
            }
            finally
            {
                travaDaConversa.Dispose();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ciclo.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new Contagens(1, 1, 2, 0), await ContarLeadAsync(harness.ConnectionString, leadId));
        }
        finally
        {
            await limpeza.LimparAsync();
        }
    }

    private static async Task<Harness> CriarHarnessAsync(DbCommandInterceptor? interceptador = null)
    {
        string conexao;
        await using (var verificacao = await PostgresTestDatabase.CriarContextoAsync())
        {
            conexao = verificacao.Database.GetConnectionString()!;
        }

        var logs = new CapturadorDeLogs();
        var travas = new TravaDeConversas();
        var configuracao = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Expurgo:IntervaloVarredura"] = "1.00:00:00",
                ["Expurgo:PrazoRetencaoMeses"] = "12",
            })
            .Build();
        var servicos = new ServiceCollection();
        servicos.AddDbContext<SolarDbContext>(opcoes =>
        {
            opcoes.UseNpgsql(conexao);
            if (interceptador is not null)
            {
                opcoes.AddInterceptors(interceptador);
            }
        });
        servicos.AddScoped<ConversaRepositorio>();
        servicos.AddSingleton(travas);
        servicos.AddSingleton<IConfiguration>(configuracao);
        servicos.AddSingleton<TimeProvider>(new RelogioFixo(Agora));
        servicos.AddLogging(opcoes =>
        {
            opcoes.ClearProviders();
            opcoes.SetMinimumLevel(LogLevel.Information);
            opcoes.AddProvider(logs);
        });
        servicos.AddSingleton<ServicoDeExpurgo>();

        var provedor = servicos.BuildServiceProvider();
        return new Harness(
            provedor,
            conexao,
            provedor.GetRequiredService<ServicoDeExpurgo>(),
            travas,
            logs);
    }

    private static SolarDbContext NovoContexto(string conexao) =>
        new(new DbContextOptionsBuilder<SolarDbContext>().UseNpgsql(conexao).Options);

    private static async Task SalvarAsync(string conexao, Action<SolarDbContext> preparar)
    {
        await using var db = NovoContexto(conexao);
        preparar(db);
        await db.SaveChangesAsync();
    }

    private static async Task ExecutarSqlAsync(string conexao, string sql)
    {
        await using var db = NovoContexto(conexao);
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<bool> SequenciaFoiUsadaAsync(string conexao, string nomeSequencia)
    {
        await using var db = NovoContexto(conexao);
        var conexaoAberta = db.Database.GetDbConnection();
        await conexaoAberta.OpenAsync();
        await using var comando = conexaoAberta.CreateCommand();
        comando.CommandText = $"SELECT is_called FROM {nomeSequencia}";
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ExisteObjetoDoCatalogoAsync(string conexao, string sql, string nome)
    {
        await using var db = NovoContexto(conexao);
        var conexaoAberta = db.Database.GetDbConnection();
        await conexaoAberta.OpenAsync();
        await using var comando = conexaoAberta.CreateCommand();
        comando.CommandText = sql;
        var parametro = comando.CreateParameter();
        parametro.ParameterName = "p0";
        parametro.Value = nome;
        comando.Parameters.Add(parametro);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task RegistrarNovaMensagemDoLeadAsync(
        string conexao,
        Guid conversaId,
        DateTimeOffset enviadaEm)
    {
        await using var db = NovoContexto(conexao);
        var conversa = await db.Conversas
            .Include(item => item.Lead)
            .Include(item => item.Mensagens)
            .SingleAsync(item => item.Id == conversaId);
        RegistrarMensagemDoLead(conversa, enviadaEm, TextoCanario);
        await db.SaveChangesAsync();
    }

    private static async Task<Contagens> ContarLeadAsync(string conexao, Guid leadId)
    {
        await using var db = NovoContexto(conexao);
        var mensagens = await (
            from mensagem in db.Mensagens
            join conversa in db.Conversas on mensagem.ConversaId equals conversa.Id
            where conversa.LeadId == leadId
            select mensagem.Id
        ).CountAsync();

        return new Contagens(
            await db.Leads.CountAsync(lead => lead.Id == leadId),
            await db.Conversas.CountAsync(conversa => conversa.LeadId == leadId),
            mensagens,
            await db.Encaminhamentos.CountAsync(encaminhamento => encaminhamento.LeadId == leadId));
    }

    private static Conversa NovaConversaComMensagem(
        DateTimeOffset contatoEm,
        string texto = "mensagem de teste")
    {
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, contatoEm);
        RegistrarMensagemDoLead(conversa, contatoEm, texto);
        return conversa;
    }

    private static void RegistrarMensagemDoLead(
        Conversa conversa,
        DateTimeOffset enviadaEm,
        string texto = "mensagem do lead")
    {
        conversa.RegistrarTurno(texto, Resposta(), enviadaEm);
    }

    private static TurnoResponse Resposta() => new(
        "Resposta de teste",
        Intencoes.Compra,
        new CamposExtraidos(),
        ProximasAcoes.ContinuarConversa,
        [],
        null);

    private static Corretor NovaConta(string perfil, DateTimeOffset em)
    {
        var email = $"s39-{perfil}-{Guid.NewGuid():N}@tests.solar.local";
        return Corretor.NovaConta(
            $"Conta S-39 {perfil}",
            email,
            email.ToUpperInvariant(),
            "11912345678",
            "hash-de-senha-de-teste",
            perfil,
            [],
            perfil == PerfisDoPainel.Corretor ? [Especialidades.Moradia] : [],
            AvisoPrivacidade.VersaoAtual,
            em);
    }

    private static async Task AssertarAuditoriaDeSucesso(CapturadorDeLogs logs, int quantidade)
    {
        var registro = Assert.Single(
            logs.Registros,
            item => item.Nivel == LogLevel.Information &&
                    item.Mensagem.Contains("Expurgo por retencao", StringComparison.Ordinal));
        var campos = registro.Propriedades.Keys
            .Where(chave => chave != "{OriginalFormat}")
            .OrderBy(chave => chave, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "HorarioUtc", "QuantidadeLeads" }, campos);
        Assert.Equal(quantidade, (int)registro.Propriedades["QuantidadeLeads"]!);
        Assert.Equal(Agora, (DateTimeOffset)registro.Propriedades["HorarioUtc"]!);
        Assert.Null(registro.Excecao);
    }

    private static void AssertarLogsSemDadosPessoais(CapturadorDeLogs logs, params Guid[] ids)
    {
        foreach (var registro in logs.Registros)
        {
            Assert.Null(registro.Excecao);
            var material = registro.Mensagem + " " +
                string.Join(" ", registro.Propriedades.Values.Select(valor => valor?.ToString()));
            Assert.DoesNotContain(NomeCanario, material, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TelefoneCanario, material, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EmailCanario, material, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TextoCanario, material, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ExcecaoCanario, material, StringComparison.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                Assert.DoesNotContain(id.ToString("D"), material, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private sealed class LimpezaDoTeste(string conexao)
    {
        private readonly HashSet<Guid> _leads = [];
        private readonly HashSet<Guid> _contas = [];

        public void AdicionarLead(Guid id) => _leads.Add(id);

        public void AdicionarConta(Guid id) => _contas.Add(id);

        public async Task LimparAsync()
        {
            await using var db = NovoContexto(conexao);
            foreach (var id in _leads)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM leads WHERE id = {id}");
            }

            foreach (var id in _contas)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM corretores WHERE id = {id}");
            }
        }
    }

    private sealed class Harness(
        ServiceProvider provedor,
        string connectionString,
        ServicoDeExpurgo servico,
        TravaDeConversas travas,
        CapturadorDeLogs logs) : IDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public ServicoDeExpurgo Servico { get; } = servico;
        public TravaDeConversas Travas { get; } = travas;
        public CapturadorDeLogs Logs { get; } = logs;

        public void Dispose() => provedor.Dispose();
    }

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private sealed record Contagens(int Leads, int Conversas, int Mensagens, int Encaminhamentos);

    private sealed record RegistroDeLog(
        LogLevel Nivel,
        string Mensagem,
        Exception? Excecao,
        IReadOnlyDictionary<string, object?> Propriedades);

    private sealed class CapturadorDeLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<RegistroDeLog> _registros = new();

        public IReadOnlyCollection<RegistroDeLog> Registros => _registros.ToArray();

        public ILogger CreateLogger(string categoria) =>
            new LoggerDeExpurgo(this, categoria == typeof(ServicoDeExpurgo).FullName);

        public void Dispose()
        {
        }

        private void Adicionar(RegistroDeLog registro) => _registros.Enqueue(registro);

        private sealed class LoggerDeExpurgo(CapturadorDeLogs destino, bool capturar) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => capturar && logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!capturar)
                {
                    return;
                }

                var propriedades = state is IEnumerable<KeyValuePair<string, object?>> pares
                    ? pares.ToDictionary(par => par.Key, par => par.Value)
                    : new Dictionary<string, object?>();
                destino.Adicionar(new RegistroDeLog(
                    logLevel,
                    formatter(state, exception),
                    exception,
                    propriedades));
            }
        }
    }

    private sealed class InterceptadorDaConsultaDeUltimaMensagem : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _consultaConcluida =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AguardarConsultaAsync() => _consultaConcluida.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (sql.Contains("mensagens", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("max(", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("papel", StringComparison.OrdinalIgnoreCase))
            {
                _consultaConcluida.TrySetResult();
            }

            return ValueTask.FromResult(result);
        }
    }
}
