using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Conversas;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public class ImoveisSugeridosPostgresTeste
{
    [Fact]
    public async Task Postgres_Invariante_DoLead_grava_nulo_DaLia_sem_imovel_grava_vazio_e_com_imovel_grava_snapshot()
    {
        using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);

        var imovel1 = new ImovelSugerido(
            Id: "sp-moema-01",
            Tipo: "apartamento",
            Bairro: "Moema",
            Quartos: 3,
            Metragem: 95,
            PrecoVenda: 1200000,
            PrecoAluguel: null,
            Motivo: "Ideal para família com 3 quartos perto do parque");

        var imovel2 = new ImovelSugerido(
            Id: "sp-moema-02",
            Tipo: "apartamento",
            Bairro: "Moema",
            Quartos: 2,
            Metragem: 70,
            PrecoVenda: 850000,
            PrecoAluguel: null,
            Motivo: "Ótimo custo-benefício na região solicitada");

        var imovel3 = new ImovelSugerido(
            Id: "sp-pinheiros-03",
            Tipo: "apartamento",
            Bairro: "Pinheiros",
            Quartos: 2,
            Metragem: 65,
            PrecoVenda: 900000,
            PrecoAluguel: null,
            Motivo: "Excelente localização com metrô próximo");

        // Turno 1: Sem sugestão de imóveis (Lia sem imóvel)
        var turnoSemImoveis = new TurnoResponse(
            Resposta: "Olá! Como posso te ajudar hoje?",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [],
            SlotEscolhido: null);

        repo.AplicarTurno(conversa, "Procuro apartamento", turnoSemImoveis, agora);

        // Turno 2: Com sugestão de 3 imóveis
        var turnoComImoveis = new TurnoResponse(
            Resposta: "Encontrei estas 3 opções excelentes para você:",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(Regiao: "Moema", PrecoMax: 1200000),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [imovel1, imovel2, imovel3],
            SlotEscolhido: null);

        repo.AplicarTurno(conversa, "Preferência em Moema até 1.2M", turnoComImoveis, agora.AddMinutes(1));

        await db.SaveChangesAsync();

        // 1. Validar consulta tipada via EF
        var mensagens = await db.Mensagens
            .AsNoTracking()
            .Where(m => m.ConversaId == conversaId)
            .OrderBy(m => m.Id)
            .ToListAsync();

        Assert.Equal(4, mensagens.Count);

        // Mensagem 1: Lead (Turno 1)
        Assert.Equal(Papeis.Lead, mensagens[0].Papel);
        Assert.Null(mensagens[0].ImoveisSugeridos);

        // Mensagem 2: DaLia sem imóveis (Turno 1) -> lista vazia, NÃO nula
        Assert.Equal(Papeis.Agente, mensagens[1].Papel);
        Assert.NotNull(mensagens[1].ImoveisSugeridos);
        Assert.Empty(mensagens[1].ImoveisSugeridos!);

        // Mensagem 3: Lead (Turno 2)
        Assert.Equal(Papeis.Lead, mensagens[2].Papel);
        Assert.Null(mensagens[2].ImoveisSugeridos);

        // Mensagem 4: DaLia com imóveis (Turno 2) -> snapshot preservado com 3 itens
        Assert.Equal(Papeis.Agente, mensagens[3].Papel);
        Assert.NotNull(mensagens[3].ImoveisSugeridos);
        Assert.Equal(3, mensagens[3].ImoveisSugeridos!.Count);
        Assert.Equal("sp-moema-01", mensagens[3].ImoveisSugeridos![0].Id);
        Assert.Equal("Ideal para família com 3 quartos perto do parque", mensagens[3].ImoveisSugeridos![0].Motivo);

        // 2. Validar diretamente no banco Postgres via SQL bruto para comprovar distinção JSONB
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                papel,
                imoveis_sugeridos IS NULL AS eh_nulo,
                CASE WHEN imoveis_sugeridos IS NOT NULL THEN imoveis_sugeridos = '[]'::jsonb ELSE false END AS eh_vazio,
                CASE WHEN imoveis_sugeridos IS NOT NULL THEN jsonb_array_length(imoveis_sugeridos) ELSE 0 END AS qtd_elementos
            FROM mensagens
            WHERE conversa_id = @cid
            ORDER BY id;
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "@cid";
        param.Value = conversaId;
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync();

        // Linha 1: Lead -> eh_nulo = true
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Papeis.Lead, reader.GetString(0));
        Assert.True(reader.GetBoolean(1)); // IS NULL = true
        Assert.False(reader.GetBoolean(2)); // eh_vazio = false
        Assert.Equal(0, reader.GetInt32(3));

        // Linha 2: Lia sem imóveis -> eh_nulo = false, eh_vazio = true ('[]'::jsonb), qtd = 0
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Papeis.Agente, reader.GetString(0));
        Assert.False(reader.GetBoolean(1)); // IS NULL = false
        Assert.True(reader.GetBoolean(2)); // eh_vazio = true
        Assert.Equal(0, reader.GetInt32(3));

        // Linha 3: Lead -> eh_nulo = true
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Papeis.Lead, reader.GetString(0));
        Assert.True(reader.GetBoolean(1)); // IS NULL = true
        Assert.False(reader.GetBoolean(2));
        Assert.Equal(0, reader.GetInt32(3));

        // Linha 4: Lia com imóveis -> eh_nulo = false, eh_vazio = false, qtd = 3
        Assert.True(await reader.ReadAsync());
        Assert.Equal(Papeis.Agente, reader.GetString(0));
        Assert.False(reader.GetBoolean(1)); // IS NULL = false
        Assert.False(reader.GetBoolean(2)); // eh_vazio = false
        Assert.Equal(3, reader.GetInt32(3)); // jsonb_array_length = 3
    }
}
