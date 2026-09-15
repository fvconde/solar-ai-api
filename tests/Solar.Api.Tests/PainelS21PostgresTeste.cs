using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class PainelS21PostgresTeste : IClassFixture<PainelApiFactory>
{
    private readonly PainelApiFactory factory;

    public PainelS21PostgresTeste(PainelApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Filtro_nao_permitido_para_corretor_comum_devolve_403_sem_vazar_fila()
    {
        var cenario = await CriarCenarioAsync();
        using var client = factory.CreateClient();
        AdicionarSessao(client, cenario.CorretorToken);

        using var resposta = await client.GetAsync("/painel/leads?filtro=visao_geral");
        var json = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
        Assert.Equal("{\"erro\":\"perfil_insuficiente\",\"perfilExigido\":\"supervisor\"}", json);
        Assert.DoesNotContain(cenario.ProprioLeadId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telefone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("total", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Lista_e_detalhe_preservam_escopo_pii_e_camel_case_no_json_cru()
    {
        var cenario = await CriarCenarioAsync();
        using var client = factory.CreateClient();
        AdicionarSessao(client, cenario.CorretorToken);

        using var lista = await client.GetAsync("/painel/leads");
        var listaJson = await lista.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);
        Assert.Contains("\"itens\"", listaJson, StringComparison.Ordinal);
        Assert.Contains("\"total\"", listaJson, StringComparison.Ordinal);
        Assert.Contains("\"nomeExibicao\"", listaJson, StringComparison.Ordinal);
        Assert.Contains("\"pedidoResumo\"", listaJson, StringComparison.Ordinal);
        Assert.Contains("\"leadStatus\"", listaJson, StringComparison.Ordinal);
        Assert.Contains("\"encaminhamentoStatus\"", listaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"telefone\"", listaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"email\"", listaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"transcricao\"", listaJson, StringComparison.Ordinal);

        using var detalhe = await client.GetAsync($"/painel/leads/{cenario.ProprioLeadId:D}");
        var detalheJson = await detalhe.Content.ReadAsStringAsync();
        var detalheContrato = await detalhe.Content.ReadFromJsonAsync<DetalheLeadPainelResponse>();

        Assert.Equal(HttpStatusCode.OK, detalhe.StatusCode);
        Assert.NotNull(detalheContrato);
        Assert.Equal(cenario.ProprioTelefone, detalheContrato!.Contato.Telefone);
        Assert.Equal(cenario.ProprioEmail, detalheContrato.Contato.Email);
        Assert.Contains("\"nomeExibicao\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"pedidoResumo\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"leadStatus\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"vinculoAtivo\"", (await client.GetStringAsync("/painel/sessao")), StringComparison.Ordinal);
        Assert.Contains($"\"telefone\":\"{cenario.ProprioTelefone}\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains($"\"email\":\"{cenario.ProprioEmail}\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"resumo\":null", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"imoveisSugeridos\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"transcricao\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"papel\":\"lead\"", detalheJson, StringComparison.Ordinal);
        Assert.Contains("\"papel\":\"lia\"", detalheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"NomeExibicao\"", detalheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PedidoResumo\"", detalheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LeadStatus\"", detalheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"VinculoAtivo\"", detalheJson, StringComparison.Ordinal);

        using var supervisorClient = factory.CreateClient();
        AdicionarSessao(supervisorClient, cenario.SupervisorToken);
        using var detalheComResumo = await supervisorClient.GetAsync(
            $"/painel/leads/{cenario.LeadComResumoId:D}");
        var resumoJson = await detalheComResumo.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detalheComResumo.StatusCode);
        Assert.Contains("\"proximoPasso\":\"retornar com opcoes\"", resumoJson, StringComparison.Ordinal);
        Assert.Contains("\"atribuidoEm\"", resumoJson, StringComparison.Ordinal);
        Assert.Contains("\"agendamento\":{\"dataHora\"", resumoJson, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"confirmado\"", resumoJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detalhe_retorna_404_identico_para_lead_inexistente_e_fora_do_escopo()
    {
        var cenario = await CriarCenarioAsync();
        using var client = factory.CreateClient();
        AdicionarSessao(client, cenario.CorretorToken);

        using var foraDoEscopo = await client.GetAsync($"/painel/leads/{cenario.ForaDaCarteiraLeadId:D}");
        using var inexistente = await client.GetAsync($"/painel/leads/{cenario.InexistenteId:D}");
        var foraJson = await foraDoEscopo.Content.ReadAsStringAsync();
        var inexistenteJson = await inexistente.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, foraDoEscopo.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Equal(inexistenteJson, foraJson);
        Assert.Equal("{\"erro\":\"lead_nao_encontrado\"}", foraJson);
    }

    [Fact]
    public async Task Fatores_sao_seis_e_valor_fica_nulo_sem_qualificacao()
    {
        var cenario = await CriarCenarioAsync();
        using var client = factory.CreateClient();
        AdicionarSessao(client, cenario.SupervisorSemVinculoToken);

        using var resposta = await client.GetAsync($"/painel/leads/{cenario.LeadSemDadosId:D}");
        var detalhe = await resposta.Content.ReadFromJsonAsync<DetalheLeadPainelResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.NotNull(detalhe);
        Assert.Null(detalhe.Qualificacao.Valor);
        Assert.Equal(6, detalhe.Qualificacao.Fatores.Count);
        Assert.Equal(
            new[] { "finalidade", "bairro", "quartos", "faixa", "prazo", "contato" },
            detalhe.Qualificacao.Fatores.Select(fator => fator.Codigo));
        Assert.All(detalhe.Qualificacao.Fatores, fator => Assert.False(fator.Preenchido));
    }

    [Fact]
    public async Task Supervisor_sem_vinculo_recebe_corretor_id_nulo_e_nao_recebe_minha_fila()
    {
        var cenario = await CriarCenarioAsync();
        using var client = factory.CreateClient();
        AdicionarSessao(client, cenario.SupervisorSemVinculoToken);

        using var resposta = await client.GetAsync("/painel/sessao");
        var sessao = await resposta.Content.ReadFromJsonAsync<SessaoPainelResponse>();
        var json = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.NotNull(sessao);
        Assert.Equal(PerfisDoPainel.Supervisor, sessao.Perfil);
        Assert.Null(sessao.CorretorId);
        Assert.False(sessao.VinculoAtivo);
        Assert.Equal(new[] { "sem_corretor", "visao_geral" }, sessao.FiltrosPermitidos);
        Assert.Equal("sem_corretor", sessao.FiltroInicial);
        Assert.Contains("\"corretorId\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"filtrosPermitidos\":[\"sem_corretor\",\"visao_geral\"]", json, StringComparison.Ordinal);
        Assert.Contains("\"filtroInicial\":\"sem_corretor\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("minha_fila", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessao_ausente_devolve_401_com_erro_de_sessao()
    {
        using var client = factory.CreateClient();

        using var resposta = await client.GetAsync("/painel/leads");
        var json = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal("{\"erro\":\"sessao_invalida\"}", json);
    }

    private async Task<Cenario> CriarCenarioAsync()
    {
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var agora = DateTimeOffset.UtcNow.AddMinutes(-10);
        var proprioEmail = $"lead-{Guid.NewGuid():N}@tests.solar.local";
        var sufixoTelefone = Convert.ToUInt64(Guid.NewGuid().ToString("N")[..15], 16) % 1_000_000_000;
        var proprioTelefone = $"5511{sufixoTelefone:D9}";

        var corretor = CriarCorretor(Guid.NewGuid(), "Corretor S21", $"corretor-{Guid.NewGuid():N}@tests.solar.local");
        var outroCorretor = CriarCorretor(Guid.NewGuid(), "Outro S21", $"outro-{Guid.NewGuid():N}@tests.solar.local");
        var supervisor = CriarCorretor(
            Guid.NewGuid(),
            "Supervisor S21",
            $"supervisor-{Guid.NewGuid():N}@tests.solar.local",
            PerfisDoPainel.Supervisor,
            vinculoAtivo: true);
        var supervisorSemVinculo = CriarCorretor(
            Guid.NewGuid(),
            "Supervisor Sem Vinculo S21",
            $"supervisor-sem-{Guid.NewGuid():N}@tests.solar.local",
            PerfisDoPainel.Supervisor,
            vinculoAtivo: false);

        var proprio = CriarConversa(
            agora,
            "Lead S21",
            new CamposExtraidos(
                Nome: "Lead S21",
                PrecoMin: 2000,
                PrecoMax: 3500,
                Quartos: 2,
                Regiao: "sul",
                Urgencia: Urgencias.Media,
                Score: 90),
            telefone: proprioTelefone,
            email: proprioEmail,
            turnos: 3);
        var foraDaCarteira = CriarConversa(
            agora.AddMinutes(1),
            "Lead Fora S21",
            new CamposExtraidos(Nome: "Lead Fora S21", Score: 20),
            turnos: 1);
        var comResumo = CriarConversa(
            agora.AddMinutes(2),
            "Lead Resumo S21",
            new CamposExtraidos(Nome: "Lead Resumo S21", Regiao: "centro"),
            turnos: 1);
        var semDados = Lead.Novo(agora.AddMinutes(3));

        proprio.Lead.RegistrarContato("Lead S21", proprioTelefone, proprioEmail, agora);

        var encaminhamentoProprio = Encaminhamento.Novo(
            proprio.Id,
            proprio.LeadId,
            corretor.Id,
            Especialidades.Moradia,
            agora);
        var encaminhamentoFora = Encaminhamento.Novo(
            foraDaCarteira.Id,
            foraDaCarteira.LeadId,
            outroCorretor.Id,
            Especialidades.Moradia,
            agora.AddMinutes(1));
        var encaminhamentoResumo = Encaminhamento.Novo(
            comResumo.Id,
            comResumo.LeadId,
            supervisor.Id,
            Especialidades.Moradia,
            agora.AddMinutes(2));
        encaminhamentoResumo.RegistrarResumo(
            new ResumoResponse(
                "perfil do lead",
                "até R$ 4 mil",
                null,
                "sem objeções registradas",
                "retornar com opcoes"));
        var slotDoResumo = Slot.Novo(
            supervisor.Id,
            DateTimeOffset.UtcNow.AddYears(1),
            DateTimeOffset.UtcNow.AddYears(1).AddHours(1));
        typeof(Slot).GetProperty(nameof(Slot.LeadId))!.SetValue(slotDoResumo, comResumo.LeadId);
        db.Slots.Add(slotDoResumo);

        var proprioToken = TokenSeguro.Criar();
        var supervisorToken = TokenSeguro.Criar();
        var supervisorSemVinculoToken = TokenSeguro.Criar();
        db.Corretores.AddRange(corretor, outroCorretor, supervisor, supervisorSemVinculo);
        db.Conversas.AddRange(proprio, foraDaCarteira, comResumo);
        db.Leads.Add(semDados);
        db.Encaminhamentos.AddRange(encaminhamentoProprio, encaminhamentoFora, encaminhamentoResumo);
        db.Sessoes.AddRange(
            SessaoCorretor.Nova(corretor.Id, TokenSeguro.Sha256(proprioToken), agora),
            SessaoCorretor.Nova(supervisor.Id, TokenSeguro.Sha256(supervisorToken), agora),
            SessaoCorretor.Nova(supervisorSemVinculo.Id, TokenSeguro.Sha256(supervisorSemVinculoToken), agora));
        await db.SaveChangesAsync();

        return new Cenario(
            proprio.LeadId,
            foraDaCarteira.LeadId,
            comResumo.LeadId,
            semDados.Id,
            Guid.NewGuid(),
            proprioTelefone,
            proprioEmail,
            proprioToken,
            supervisorToken,
            supervisorSemVinculoToken);
    }

    private static Conversa CriarConversa(
        DateTimeOffset inicio,
        string nome,
        CamposExtraidos campos,
        string? telefone = null,
        string? email = null,
        int turnos = 1)
    {
        var conversa = Conversa.Nova(Guid.NewGuid(), Canais.Web, inicio);
        for (var indice = 0; indice < turnos; indice++)
        {
            var imoveis = indice == 0
                ? new[]
                {
                    new ImovelSugerido(
                        "imovel-s21",
                        "apartamento",
                        "sul",
                        2,
                        70,
                        null,
                        3000,
                        "perto do metrô"),
                }
                : Array.Empty<ImovelSugerido>();
            var turno = new TurnoResponse(
                $"Resposta da Lia {indice + 1}",
                Intencoes.Aluguel,
                indice == 0 ? campos : new CamposExtraidos(),
                ProximasAcoes.ContinuarConversa,
                imoveis,
                null);
            conversa.RegistrarTurno($"Mensagem do lead {indice + 1}", turno, inicio.AddMinutes(indice));
        }

        if (telefone is not null || email is not null)
        {
            conversa.Lead.RegistrarContato(nome, telefone, email, inicio);
        }

        return conversa;
    }

    private static Corretor CriarCorretor(
        Guid id,
        string nome,
        string email,
        string perfil = PerfisDoPainel.Corretor,
        bool vinculoAtivo = true)
    {
        var corretor = (Corretor)Activator.CreateInstance(typeof(Corretor), nonPublic: true)!;
        typeof(Corretor).GetProperty(nameof(Corretor.Id))!.SetValue(corretor, id);
        typeof(Corretor).GetProperty(nameof(Corretor.Nome))!.SetValue(corretor, nome);
        typeof(Corretor).GetProperty(nameof(Corretor.Especialidade))!
            .SetValue(corretor, Especialidades.Moradia);
        typeof(Corretor).GetProperty(nameof(Corretor.ContatoInterno))!
            .SetValue(corretor, email);
        typeof(Corretor).GetProperty(nameof(Corretor.Email))!.SetValue(corretor, email);
        typeof(Corretor).GetProperty(nameof(Corretor.EmailNormalizado))!
            .SetValue(corretor, email.ToLowerInvariant());
        typeof(Corretor).GetProperty(nameof(Corretor.Regioes))!
            .SetValue(corretor, new List<string> { "sul", "centro" });
        typeof(Corretor).GetProperty(nameof(Corretor.Ativo))!.SetValue(corretor, true);
        typeof(Corretor).GetProperty(nameof(Corretor.Perfil))!.SetValue(corretor, perfil);
        typeof(Corretor).GetProperty(nameof(Corretor.VinculoAtivo))!.SetValue(corretor, vinculoAtivo);
        typeof(Corretor).GetProperty(nameof(Corretor.CriadoEm))!
            .SetValue(corretor, DateTimeOffset.UtcNow.AddMinutes(-20));
        return corretor;
    }

    private static void AdicionarSessao(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{CorretorAuthenticationDefaults.CookieName}={token}");
    }

    private sealed record Cenario(
        Guid ProprioLeadId,
        Guid ForaDaCarteiraLeadId,
        Guid LeadComResumoId,
        Guid LeadSemDadosId,
        Guid InexistenteId,
        string ProprioTelefone,
        string ProprioEmail,
        string CorretorToken,
        string SupervisorToken,
        string SupervisorSemVinculoToken);
}
