using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;

namespace Solar.Api.Tests;

public class PainelTeste
{
    private const string ChaveTeste = "solar_secret_test_key_123";
    private static readonly DateTimeOffset Agora = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);

    private static SolarDbContext CriarBanco()
    {
        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new SolarDbContext(options);
    }

    private static IConfiguration CriarConfiguracao(string? chave = ChaveTeste)
    {
        var dados = new Dictionary<string, string?>();
        if (chave is not null)
        {
            dados["Seguranca:ChavePrivacidade"] = chave;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dados).Build();
    }

    private static DefaultHttpContext CriarHttpContext(string? chavePrivacidade = ChaveTeste)
    {
        var context = new DefaultHttpContext();
        if (chavePrivacidade is not null)
        {
            context.Request.Headers[AutorizacaoPrivacidade.HeaderChavePrivacidade] = chavePrivacidade;
        }
        return context;
    }

    private static PainelController CriarController(
        SolarDbContext db,
        IConfiguration? config = null,
        DefaultHttpContext? httpContext = null)
    {
        var controller = new PainelController(db, config ?? CriarConfiguracao())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext ?? CriarHttpContext()
            }
        };
        return controller;
    }

    private static Corretor CriarCorretor(Guid id, string nome, string especialidade = "moradia", bool ativo = true)
    {
        var corretor = (Corretor)Activator.CreateInstance(typeof(Corretor), nonPublic: true)!;
        typeof(Corretor).GetProperty(nameof(Corretor.Id))!.SetValue(corretor, id);
        typeof(Corretor).GetProperty(nameof(Corretor.Nome))!.SetValue(corretor, nome);
        typeof(Corretor).GetProperty(nameof(Corretor.Especialidade))!.SetValue(corretor, especialidade);
        typeof(Corretor).GetProperty(nameof(Corretor.ContatoInterno))!.SetValue(corretor, $"{nome.ToLowerInvariant()}@solar.local");
        typeof(Corretor).GetProperty(nameof(Corretor.Regioes))!.SetValue(corretor, new List<string> { "sul", "oeste" });
        typeof(Corretor).GetProperty(nameof(Corretor.Ativo))!.SetValue(corretor, ativo);
        typeof(Corretor).GetProperty(nameof(Corretor.CriadoEm))!.SetValue(corretor, Agora);
        return corretor;
    }

    [Fact]
    public async Task Sem_chave_no_servidor_painel_falha_fechado_com_503()
    {
        using var db = CriarBanco();
        var configSemChave = CriarConfiguracao(null);
        var controller = CriarController(db, config: configSemChave);

        var resCorretores = await controller.ListarCorretoresAsync(default);
        var objCorretores = Assert.IsType<ObjectResult>(resCorretores.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objCorretores.StatusCode);

        var resLeads = await controller.ListarLeadsAsync(Guid.NewGuid().ToString(), null, null, default);
        var objLeads = Assert.IsType<ObjectResult>(resLeads.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objLeads.StatusCode);
    }

    [Fact]
    public async Task Requisicao_anonima_no_painel_devolve_401_e_nenhum_dado()
    {
        using var db = CriarBanco();
        var httpAnonimo = new DefaultHttpContext(); // Sem header de chave
        var controller = CriarController(db, httpContext: httpAnonimo);

        // GET /painel/corretores anônimo -> 401
        var resCorretores = await controller.ListarCorretoresAsync(default);
        var objCorretores = Assert.IsType<ObjectResult>(resCorretores.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objCorretores.StatusCode);

        // GET /painel/leads anônimo -> 401
        var resLeads = await controller.ListarLeadsAsync(Guid.NewGuid().ToString(), null, null, default);
        var objLeads = Assert.IsType<ObjectResult>(resLeads.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objLeads.StatusCode);
    }

    [Fact]
    public async Task Chave_invalida_no_painel_devolve_403()
    {
        using var db = CriarBanco();
        var httpInvalido = CriarHttpContext("chave_errada");
        var controller = CriarController(db, httpContext: httpInvalido);

        var resCorretores = await controller.ListarCorretoresAsync(default);
        var objCorretores = Assert.IsType<ObjectResult>(resCorretores.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objCorretores.StatusCode);

        var resLeads = await controller.ListarLeadsAsync(Guid.NewGuid().ToString(), null, null, default);
        var objLeads = Assert.IsType<ObjectResult>(resLeads.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objLeads.StatusCode);
    }

    [Fact]
    public async Task Com_chave_valida_mas_sem_corretor_id_leads_devolve_401()
    {
        using var db = CriarBanco();
        var controller = CriarController(db);

        // Sem cabeçalho X-Corretor-Id
        var resultadoSemCabecalho = await controller.ListarLeadsAsync(null, null, null, default);
        var obj = Assert.IsType<ObjectResult>(resultadoSemCabecalho.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, obj.StatusCode);

        // GUID inválido
        var resultadoInvalido = await controller.ListarLeadsAsync("guid-invalido", null, null, default);
        var objInvalido = Assert.IsType<ObjectResult>(resultadoInvalido.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objInvalido.StatusCode);
    }

    [Fact]
    public async Task Com_corretor_inexistente_ou_inativo_leads_devolve_403()
    {
        using var db = CriarBanco();
        var inativoId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(inativoId, "Corretor Inativo", ativo: false));
        await db.SaveChangesAsync();

        var controller = CriarController(db);

        // Inexistente
        var resInexistente = await controller.ListarLeadsAsync(Guid.NewGuid().ToString(), null, null, default);
        var objInexistente = Assert.IsType<ObjectResult>(resInexistente.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objInexistente.StatusCode);

        // Inativo
        var resInativo = await controller.ListarLeadsAsync(inativoId.ToString(), null, null, default);
        var objInativo = Assert.IsType<ObjectResult>(resInativo.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objInativo.StatusCode);
    }

    [Fact]
    public async Task Ordenacao_coloca_score_100_acima_do_45()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga"));

        var lead45 = Lead.Novo(Agora.AddMinutes(-30));
        lead45.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 45", Score: 45), Agora.AddMinutes(-30));

        var lead100 = Lead.Novo(Agora.AddMinutes(-20));
        lead100.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 100", Score: 100), Agora.AddMinutes(-20));

        var lead70 = Lead.Novo(Agora.AddMinutes(-10));
        lead70.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 70", Score: 70), Agora.AddMinutes(-10));

        db.Leads.AddRange(lead45, lead100, lead70);
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var acao = await controller.ListarLeadsAsync(corretorId.ToString(), null, null, default);

        var ok = Assert.IsType<OkObjectResult>(acao.Result);
        var resposta = Assert.IsType<FilaLeadsResponse>(ok.Value);

        Assert.Equal(3, resposta.Total);
        Assert.Equal(100, resposta.Leads[0].Score);
        Assert.Equal("Lead 100", resposta.Leads[0].Nome);
        Assert.Equal(70, resposta.Leads[1].Score);
        Assert.Equal("Lead 70", resposta.Leads[1].Nome);
        Assert.Equal(45, resposta.Leads[2].Score);
        Assert.Equal("Lead 45", resposta.Leads[2].Nome);
    }

    [Fact]
    public async Task Filtro_meus_leads_com_dois_corretores_e_quatro_leads_muda_a_lista()
    {
        using var db = CriarBanco();
        var corretorAId = Guid.NewGuid();
        var corretorBId = Guid.NewGuid();

        var corretorA = CriarCorretor(corretorAId, "Helena Braga");
        var corretorB = CriarCorretor(corretorBId, "Rafael Nunes");
        db.Corretores.AddRange(corretorA, corretorB);

        var lead1 = Lead.Novo(Agora.AddMinutes(-40));
        lead1.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 1", Score: 90), Agora.AddMinutes(-40));

        var lead2 = Lead.Novo(Agora.AddMinutes(-30));
        lead2.Fundir(Intencoes.Aluguel, new CamposExtraidos(Nome: "Lead 2", Score: 80), Agora.AddMinutes(-30));

        var lead3 = Lead.Novo(Agora.AddMinutes(-20));
        lead3.Fundir(Intencoes.Investimento, new CamposExtraidos(Nome: "Lead 3", Score: 70), Agora.AddMinutes(-20));

        var lead4 = Lead.Novo(Agora.AddMinutes(-10));
        lead4.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead 4", Score: 60), Agora.AddMinutes(-10));

        db.Leads.AddRange(lead1, lead2, lead3, lead4);

        var conversa1Id = Guid.NewGuid();
        var conversa2Id = Guid.NewGuid();
        var conversa3Id = Guid.NewGuid();

        db.Encaminhamentos.AddRange(
            Encaminhamento.Novo(conversa1Id, lead1.Id, corretorAId, Especialidades.Moradia, Agora),
            Encaminhamento.Novo(conversa2Id, lead2.Id, corretorAId, Especialidades.Moradia, Agora),
            Encaminhamento.Novo(conversa3Id, lead3.Id, corretorBId, Especialidades.Investimento, Agora)
        );

        await db.SaveChangesAsync();

        var controller = CriarController(db);

        // Lista geral vista pelo Corretor A -> 4 leads
        var respostaGeral = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorAId.ToString(), null, false, default)).Result).Value);
        Assert.Equal(4, respostaGeral.Total);

        // Filtro "meus leads" para Corretor A -> 2 leads (Lead 1 e Lead 2)
        var respostaMeusA = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorAId.ToString(), null, true, default)).Result).Value);
        Assert.Equal(2, respostaMeusA.Total);
        Assert.All(respostaMeusA.Leads, l => Assert.Equal(corretorAId, l.CorretorId));
        Assert.Contains(respostaMeusA.Leads, l => l.Nome == "Lead 1");
        Assert.Contains(respostaMeusA.Leads, l => l.Nome == "Lead 2");

        // Filtro "meus leads" para Corretor B -> 1 lead (Lead 3)
        var respostaMeusB = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorBId.ToString(), null, true, default)).Result).Value);
        Assert.Single(respostaMeusB.Leads);
        Assert.Equal("Lead 3", respostaMeusB.Leads[0].Nome);
        Assert.Equal(corretorBId, respostaMeusB.Leads[0].CorretorId);
    }

    [Fact]
    public async Task Lead_sem_corretor_aparece_marcado_e_nao_escondido()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga"));

        var leadSemCorretor = Lead.Novo(Agora);
        leadSemCorretor.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Lead Avulso", Score: 50), Agora);
        db.Leads.Add(leadSemCorretor);
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var resposta = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorId.ToString(), null, null, default)).Result).Value);

        Assert.Single(resposta.Leads);
        var item = resposta.Leads[0];
        Assert.Equal("Lead Avulso", item.Nome);
        Assert.Null(item.CorretorId);
        Assert.Null(item.CorretorNome);
    }

    [Fact]
    public async Task Payload_do_lead_nao_contem_telefone_nem_email()
    {
        // Confere reflexivamente que LeadPainelItem não possui campos de telefone ou email (LGPD)
        var propriedades = typeof(LeadPainelItem).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.DoesNotContain("telefone", propriedades);
        Assert.DoesNotContain("email", propriedades);
    }

    [Fact]
    public async Task Fila_vazia_devolve_lista_vazia_e_total_zero()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga"));
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var resposta = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorId.ToString(), null, null, default)).Result).Value);

        Assert.Equal(0, resposta.Total);
        Assert.Empty(resposta.Leads);
    }

    [Fact]
    public async Task Filtro_por_intencao_restringe_leads_corretamente()
    {
        using var db = CriarBanco();
        var corretorId = Guid.NewGuid();
        db.Corretores.Add(CriarCorretor(corretorId, "Helena Braga"));

        var leadCompra = Lead.Novo(Agora);
        leadCompra.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Comprador", Score: 90), Agora);

        var leadAluguel = Lead.Novo(Agora);
        leadAluguel.Fundir(Intencoes.Aluguel, new CamposExtraidos(Nome: "Inquilino", Score: 75), Agora);

        db.Leads.AddRange(leadCompra, leadAluguel);
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var respostaCompra = Assert.IsType<FilaLeadsResponse>(
            Assert.IsType<OkObjectResult>((await controller.ListarLeadsAsync(corretorId.ToString(), Intencoes.Compra, null, default)).Result).Value);

        Assert.Single(respostaCompra.Leads);
        Assert.Equal("Comprador", respostaCompra.Leads[0].Nome);
        Assert.Equal(Intencoes.Compra, respostaCompra.Leads[0].Intencao);
    }

    [Fact]
    public async Task Listar_corretores_retorna_apenas_corretores_ativos_com_autorizacao()
    {
        using var db = CriarBanco();
        var ativoId = Guid.NewGuid();
        var inativoId = Guid.NewGuid();

        db.Corretores.AddRange(
            CriarCorretor(ativoId, "Corretor Ativo", ativo: true),
            CriarCorretor(inativoId, "Corretor Inativo", ativo: false)
        );
        await db.SaveChangesAsync();

        var controller = CriarController(db);
        var resultado = await controller.ListarCorretoresAsync(default);

        var ok = Assert.IsType<OkObjectResult>(resultado.Result);
        var lista = Assert.IsType<List<CorretorIdentificacao>>(ok.Value);

        Assert.Single(lista);
        Assert.Equal(ativoId, lista[0].Id);
        Assert.Equal("Corretor Ativo", lista[0].Nome);
    }
}
