using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class MarcosMigrationPostgresTeste
{
    [Fact]
    public async Task Information_schema_confirma_cinco_novos_campos_timestamptz_anulaveis_e_sem_default()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        var conexao = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conexao.State != System.Data.ConnectionState.Open)
        {
            await conexao.OpenAsync();
        }

        var colunasEsperadas = new[]
        {
            "intencao_em",
            "essenciais_em",
            "encaminhada_em",
            "corretor_atribuido_em",
            "primeiro_reengajamento_em",
        };

        const string sql = """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_name = 'conversas'
              AND column_name = ANY(@nomes);
            """;

        await using var cmd = new NpgsqlCommand(sql, conexao);
        cmd.Parameters.AddWithValue("nomes", colunasEsperadas);

        await using var reader = await cmd.ExecuteReaderAsync();
        var colunasEncontradas = new Dictionary<string, (string DataType, string IsNullable, object? Default)>();

        while (await reader.ReadAsync())
        {
            var nome = reader.GetString(0);
            var tipo = reader.GetString(1);
            var anulavel = reader.GetString(2);
            var padrao = reader.IsDBNull(3) ? null : reader.GetValue(3);

            colunasEncontradas[nome] = (tipo, anulavel, padrao);
        }

        Assert.Equal(5, colunasEncontradas.Count);
        foreach (var nome in colunasEsperadas)
        {
            Assert.True(colunasEncontradas.ContainsKey(nome));
            var (dataType, isNullable, columnDefault) = colunasEncontradas[nome];
            Assert.Equal("timestamp with time zone", dataType);
            Assert.Equal("YES", isNullable);
            Assert.Null(columnDefault);
        }
    }

    [Fact]
    public async Task Registro_metricas_contem_linha_unica_e_novo_migrate_nao_altera_instante()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();

        var registros = await db.RegistroMetricas.AsNoTracking().ToListAsync();
        Assert.Single(registros);

        var registro = registros[0];
        Assert.Equal(1, registro.Id);
        Assert.NotEqual(default, registro.HistoricoDesde);

        var instanteOriginal = registro.HistoricoDesde;

        await db.Database.MigrateAsync();

        var registrosAposMigrate = await db.RegistroMetricas.AsNoTracking().ToListAsync();
        Assert.Single(registrosAposMigrate);
        Assert.Equal(1, registrosAposMigrate[0].Id);
        Assert.Equal(instanteOriginal, registrosAposMigrate[0].HistoricoDesde);
    }

    [Fact]
    public async Task Conversa_com_data_anterior_ao_registro_e_perfil_completo_nao_tem_marcos_e_preserva_nulos()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();

        var registro = await db.RegistroMetricas.AsNoTracking().SingleAsync(r => r.Id == 1);
        var dataAnterior = registro.HistoricoDesde.AddDays(-10);

        var conversaId = Guid.NewGuid();
        var conversa = Conversa.Nova(conversaId, Canais.Web, dataAnterior);

        conversa.Lead.Fundir(
            Intencoes.Compra,
            new CamposExtraidos(
                Nome: "Lead Antigo",
                Regiao: "Pinheiros",
                PrecoMax: 850000,
                Quartos: 3,
                Urgencia: Urgencias.Alta,
                Score: 80),
            dataAnterior);

        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        try
        {
            var recarregada = await db.Conversas
                .AsNoTracking()
                .SingleAsync(c => c.Id == conversaId);

            Assert.Null(recarregada.IntencaoEm);
            Assert.Null(recarregada.EssenciaisEm);
            Assert.Null(recarregada.EncaminhadaEm);
            Assert.Null(recarregada.CorretorAtribuidoEm);
            Assert.Null(recarregada.PrimeiroReengajamentoEm);
        }
        finally
        {
            var paraRemover = await db.Conversas
                .Include(c => c.Lead)
                .FirstOrDefaultAsync(c => c.Id == conversaId);

            if (paraRemover is not null)
            {
                db.Conversas.Remove(paraRemover);
                db.Leads.Remove(paraRemover.Lead);
                await db.SaveChangesAsync();
            }
        }
    }
}
