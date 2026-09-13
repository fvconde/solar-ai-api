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

    [Fact]
    public async Task Postgres_Subtarefa3_HistoricoCompleto_preenche_imoveis_sugeridos_no_MensagemDaConversa()
    {
        using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);

        var imovel1 = new ImovelSugerido("sp-01", "apartamento", "Pinheiros", 2, 60, 750000, 3500, "Perto de transporte");
        var imovel2 = new ImovelSugerido("sp-02", "casa", "Butantã", 3, 120, 950000, 4200, "Espaço amplo com quintal");

        var turno = new TurnoResponse(
            Resposta: "Veja estes dois imóveis:",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [imovel1, imovel2],
            SlotEscolhido: null);

        repo.AplicarTurno(conversa, "Busco 2 ou 3 quartos na zona oeste", turno, agora);
        await db.SaveChangesAsync();

        var historico = await repo.HistoricoCompletoAsync(conversaId, corretor: null, agendaAtual: [], default);

        Assert.Equal(2, historico.Count);

        // Mensagem 1: Lead
        Assert.Equal(Papeis.Lead, historico[0].Papel);
        Assert.Null(historico[0].ImoveisSugeridos);

        // Mensagem 2: DaLia
        Assert.Equal(Papeis.Agente, historico[1].Papel);
        Assert.NotNull(historico[1].ImoveisSugeridos);
        Assert.Equal(2, historico[1].ImoveisSugeridos!.Count);
        Assert.Equal("sp-01", historico[1].ImoveisSugeridos![0].Id);
        Assert.Equal("Perto de transporte", historico[1].ImoveisSugeridos![0].Motivo);
        Assert.Equal("sp-02", historico[1].ImoveisSugeridos![1].Id);
        Assert.Equal("Espaço amplo com quintal", historico[1].ImoveisSugeridos![1].Motivo);
    }

    [Fact]
    public async Task Postgres_Subtarefa5_ObterImoveisSugeridosAsync_devolve_uniao_sem_duplicar_id()
    {
        using var db = await PostgresTestDatabase.CriarContextoAsync();
        var repo = new ConversaRepositorio(db);
        var agora = DateTimeOffset.UtcNow;

        var conversaId = Guid.NewGuid();
        var conversa = await repo.ObterOuCriarAsync(conversaId, agora, default);

        var imovelA = new ImovelSugerido("imovel-A", "apto", "Moema", 2, 70, 800000, null, "Opção A");
        var imovelB = new ImovelSugerido("imovel-B", "apto", "Moema", 3, 90, 1100000, null, "Opção B");
        var imovelC = new ImovelSugerido("imovel-C", "apto", "Vila Mariana", 2, 65, 750000, null, "Opção C");

        // Turno 1 recomenda [A, B]
        var turno1 = new TurnoResponse(
            Resposta: "Opções em Moema:",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [imovelA, imovelB],
            SlotEscolhido: null);
        repo.AplicarTurno(conversa, "Busco em Moema", turno1, agora);

        // Turno 2 recomenda [B, C] (B duplicado entre turnos)
        var turno2 = new TurnoResponse(
            Resposta: "Revisando com opção em Vila Mariana:",
            Intencao: Intencoes.Compra,
            CamposExtraidos: new CamposExtraidos(),
            ProximaAcao: ProximasAcoes.ContinuarConversa,
            ImoveisSugeridos: [imovelB, imovelC],
            SlotEscolhido: null);
        repo.AplicarTurno(conversa, "Aceito Vila Mariana também", turno2, agora.AddMinutes(2));

        await db.SaveChangesAsync();

        // Leitura agregada para o S-18
        var imoveisAgregados = await repo.ObterImoveisSugeridosAsync(conversaId, default);

        Assert.Equal(3, imoveisAgregados.Count);
        Assert.Equal("imovel-A", imoveisAgregados[0].Id);
        Assert.Equal("imovel-B", imoveisAgregados[1].Id);
        Assert.Equal("imovel-C", imoveisAgregados[2].Id);
    }
}
