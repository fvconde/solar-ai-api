using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

public sealed partial class ExclusaoTitularPostgresTeste
{
    [Fact]
    public async Task Lead_muda_por_dedupe_durante_espera_da_trava_e_exclusao_exige_nova_inspecao()
    {
        var observador = new ObservadorInspecao();
        using var app = CriarAplicacao(observador);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var telefone = TelefoneUnico();
        await RegistrarContatoAsync(cliente, b.Id, telefone, null);
        AdicionarCookie(cliente, a.Cookie!);
        observador.LeadInspecionado = a.LeadId;
        var travas = app.Services.GetRequiredService<TravaDeConversas>();
        Task<HttpResponseMessage> requisicao;
        string antesA, antesB;

        using (await travas.TravarMultiplasAsync([a.Id, a.LeadId], default))
        {
            requisicao = cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
            await observador.Inspecao.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var repo = new ConversaRepositorio(db);
            var conversa = (await repo.ObterParaEscritaAsync(a.Id, default))!;
            Assert.Equal(b.LeadId, await repo.RegistrarContatoAsync(conversa,
                new ContatoRequest("Contato sintetico T2", telefone, null), DateTimeOffset.UtcNow, default));
            antesA = await FotografarAsync(a.Id);
            antesB = await FotografarAsync(b.Id);
        }

        using var resposta = await requisicao.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        AssertSemRemocaoCookie(resposta);
        Assert.Equal(antesA, await FotografarAsync(a.Id));
        Assert.Equal(antesB, await FotografarAsync(b.Id));
        using var repeticao = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
        await AssertEscopoAsync(repeticao, "apenas_conversa", false, b);
        await AssertConversaAusenteAsync(a.Id);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    [Fact]
    public async Task Vinculo_novo_por_dedupe_durante_espera_da_trava_impede_cascata()
    {
        var observador = new ObservadorInspecao();
        using var app = CriarAplicacao(observador);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        await SemearRegistrosAsync(a.Id, b.Id);
        var telefone = TelefoneUnico();
        await RegistrarContatoAsync(cliente, a.Id, telefone, null);
        AdicionarCookie(cliente, a.Cookie!);
        observador.LeadInspecionado = a.LeadId;
        var travas = app.Services.GetRequiredService<TravaDeConversas>();
        Task<HttpResponseMessage> requisicao;
        string antesA, antesB;

        using (await travas.TravarMultiplasAsync([a.Id, a.LeadId], default))
        {
            requisicao = cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
            await observador.Inspecao.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var repo = new ConversaRepositorio(db);
            var conversa = (await repo.ObterParaEscritaAsync(b.Id, default))!;
            Assert.Equal(a.LeadId, await repo.RegistrarContatoAsync(conversa,
                new ContatoRequest("Contato sintetico T2", telefone, null), DateTimeOffset.UtcNow, default));
            antesA = await FotografarAsync(a.Id);
            antesB = await FotografarAsync(b.Id);
        }

        using var resposta = await requisicao.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        AssertSemRemocaoCookie(resposta);
        Assert.Equal(antesA, await FotografarAsync(a.Id));
        Assert.Equal(antesB, await FotografarAsync(b.Id));
        using var repeticao = await cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
        await AssertEscopoAsync(repeticao, "apenas_conversa", false, b);
        await AssertConversaAusenteAsync(a.Id);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("conta")]
    public async Task Prova_alterada_durante_espera_da_trava_e_revalidada_e_recusada(string alteracao)
    {
        var observador = new ObservadorInspecao();
        using var app = CriarAplicacao(observador);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var conta = await CriarContaAsync(app);
        await SemearRegistrosAsync(a.Id);
        AdicionarCookie(cliente, a.Cookie!);
        observador.LeadInspecionado = a.LeadId;
        var travas = app.Services.GetRequiredService<TravaDeConversas>();
        Task<HttpResponseMessage> requisicao;
        string antes;

        using (await travas.TravarMultiplasAsync([a.Id, a.LeadId], default))
        {
            requisicao = cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
            await observador.Inspecao.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            var conversa = await db.Conversas.SingleAsync(c => c.Id == a.Id);
            if (alteracao == "hash") conversa.DefinirChaveExclusaoHash(TokenSeguro.Sha256(TokenSeguro.Criar()));
            else conversa.VincularConta(conta.Id);
            await db.SaveChangesAsync();
            antes = await FotografarAsync(a.Id);
        }

        using var resposta = await requisicao.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        AssertSemRemocaoCookie(resposta);
        Assert.Equal(antes, await FotografarAsync(a.Id));
    }

    [Fact]
    public async Task Mudanca_de_dono_de_conversa_vinculada_durante_espera_limita_exclusao_a_provada()
    {
        var observador = new ObservadorInspecao();
        using var app = CriarAplicacao(observador);
        var dona = await CriarContaAsync(app);
        var outra = await CriarContaAsync(app);
        using var cliente = await EntrarAsync(app, dona);
        var a = await CriarComContaAsync(cliente);
        var b = await CriarComContaAsync(cliente);
        var telefone = TelefoneUnico();
        await RegistrarContatoAsync(cliente, a.Id, telefone, null);
        Assert.Equal(a.LeadId, await RegistrarContatoAsync(cliente, b.Id, telefone, null));
        await SemearRegistrosAsync(a.Id, b.Id);
        observador.LeadInspecionado = a.LeadId;
        var travas = app.Services.GetRequiredService<TravaDeConversas>();
        Task<HttpResponseMessage> requisicao;
        string antesB;

        using (await travas.TravarMultiplasAsync([a.Id, b.Id, a.LeadId], default))
        {
            requisicao = cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
            await observador.Inspecao.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = await PostgresTestDatabase.CriarContextoAsync();
            (await db.Conversas.SingleAsync(c => c.Id == b.Id)).VincularConta(outra.Id);
            await db.SaveChangesAsync();
            antesB = await FotografarAsync(b.Id);
        }

        using var resposta = await requisicao.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertEscopoAsync(resposta, "apenas_conversa", false, b);
        await AssertConversaAusenteAsync(a.Id);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    [Fact]
    public async Task Dedupe_concorrente_apos_revalidacao_nao_introduz_conversa_sem_prova_na_cascata()
    {
        var pausa = new PausaAntesDeExcluir();
        using var app = CriarAplicacao(pausa);
        using var cliente = CriarCliente(app);
        var a = await CriarAnonimaAsync(cliente);
        var b = await CriarAnonimaAsync(cliente);
        var telefone = TelefoneUnico();
        await RegistrarContatoAsync(cliente, a.Id, telefone, null);
        await SemearRegistrosAsync(a.Id, b.Id);
        var antesB = await FotografarAsync(b.Id);
        AdicionarCookie(cliente, a.Cookie!);
        pausa.Ativa = true;
        var requisicao = cliente.DeleteAsync($"/conversas/{a.Id:D}/titular");
        var processoDedupe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? dedupe = null;
        try
        {
            await pausa.Exclusao.Task.WaitAsync(TimeSpan.FromSeconds(10));
            dedupe = Task.Run(async () =>
            {
                await using var db = await PostgresTestDatabase.CriarContextoAsync();
                await db.Database.OpenConnectionAsync();
                processoDedupe.SetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
                var repo = new ConversaRepositorio(db);
                var conversa = (await repo.ObterParaEscritaAsync(b.Id, default))!;
                try
                {
                    await repo.RegistrarContatoAsync(conversa,
                        new ContatoRequest("Contato sintetico T2", telefone, null), DateTimeOffset.UtcNow, default);
                    return true;
                }
                catch (DbUpdateException)
                {
                    return false;
                }
            });
            var pid = await processoDedupe.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await EsperarBloqueioPostgresAsync(pid);
            Assert.False(dedupe.IsCompleted);
        }
        finally
        {
            pausa.Liberar.TrySetResult();
        }

        using var resposta = await requisicao.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertEscopoAsync(resposta, "lead_e_vinculos", true, b);
        Assert.NotNull(dedupe);
        Assert.False(await dedupe.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertConversaAusenteAsync(a.Id);
        await AssertLeadAusenteAsync(a.LeadId);
        Assert.Equal(antesB, await FotografarAsync(b.Id));
    }

    private sealed class ObservadorInspecao : DbCommandInterceptor
    {
        public Guid? LeadInspecionado { get; set; }
        public TaskCompletionSource Inspecao { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (LeadInspecionado is { } id && command.CommandText.Contains("SELECT c.id", StringComparison.Ordinal)
                && command.CommandText.Contains("WHERE c.lead_id =", StringComparison.Ordinal)
                && command.Parameters.Cast<DbParameter>().Any(p => p.Value is Guid valor && valor == id))
                Inspecao.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PausaAntesDeExcluir : DbCommandInterceptor
    {
        public bool Ativa { get; set; }
        public TaskCompletionSource Exclusao { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Ativa && command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal))
            {
                Exclusao.TrySetResult();
                await Liberar.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private static async Task EsperarBloqueioPostgresAsync(int pid)
    {
        await using var conexao = new NpgsqlConnection(PostgresTestDatabase.ObterConexaoParaAplicacao());
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid", conexao);
        comando.Parameters.AddWithValue("pid", pid);
        var limite = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < limite)
        {
            if (await comando.ExecuteScalarAsync() is string tipo && tipo == "Lock") return;
            await Task.Delay(20);
        }
        Assert.Fail("A dedupe nao ficou bloqueada no Postgres durante a exclusao.");
    }
}
