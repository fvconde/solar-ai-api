using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solar.Api.Contracts;
using Solar.Api.Controllers;
using Solar.Api.Conversas;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

public class ConsentimentoTeste
{
    private static SolarDbContext CriarBanco()
    {
        var options = new DbContextOptionsBuilder<SolarDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SolarDbContext(options);
    }

    private static ConversasController CriarController(SolarDbContext db) => new(
        new ConversaRepositorio(db),
        null!,
        null!,
        null!,
        new TravaDeConversas(),
        null!,
        new ConfigurationBuilder().Build(),
        null!,
        NullLogger<ConversasController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Fact]
    public async Task Registrar_consentimento_cria_lead_e_conversa_sem_mensagem()
    {
        using var db = CriarBanco();
        var controller = CriarController(db);
        var conversaId = Guid.NewGuid();

        var resultado = await controller.RegistrarConsentimento(
            conversaId,
            new ConsentimentoRequest(AvisoPrivacidade.VersaoAtual),
            default);

        var ok = Assert.IsType<OkObjectResult>(resultado.Result);
        var resposta = Assert.IsType<ConsentimentoResponse>(ok.Value);
        var lead = await db.Leads.SingleAsync();

        Assert.Equal(conversaId, resposta.ConversaId);
        Assert.Equal(lead.Id, resposta.LeadId);
        Assert.NotNull(lead.ConsentimentoEm);
        Assert.Equal(AvisoPrivacidade.VersaoAtual, lead.VersaoAvisoPrivacidade);
        Assert.Empty(db.Mensagens);
    }

    [Fact]
    public async Task Versao_invalida_nao_cria_lead()
    {
        using var db = CriarBanco();
        var controller = CriarController(db);

        var resultado = await controller.RegistrarConsentimento(
            Guid.NewGuid(),
            new ConsentimentoRequest("versao-invalida"),
            default);

        var problema = Assert.IsType<ObjectResult>(resultado.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problema.StatusCode);
        Assert.Empty(db.Leads);
    }

    [Fact]
    public async Task Kickoff_sem_consentimento_retorna_conflito_e_nao_cria_lead()
    {
        using var db = CriarBanco();
        var controller = CriarController(db);

        var resultado = await controller.Enviar(
            Guid.NewGuid(),
            new NovaMensagemRequest("Olá"),
            default);

        var problema = Assert.IsType<ObjectResult>(resultado.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problema.StatusCode);
        Assert.Empty(db.Leads);
        Assert.Empty(db.Mensagens);
    }

    [Fact]
    public void Mesmo_aviso_preserva_o_primeiro_carimbo()
    {
        var criadoEm = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);
        var repetidoEm = criadoEm.AddMinutes(5);
        var lead = Solar.Api.Dominio.Lead.Novo(criadoEm);

        lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, criadoEm);
        lead.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, repetidoEm);

        Assert.Equal(criadoEm, lead.ConsentimentoEm);
    }

    [Fact]
    public async Task Dedupe_transfere_consentimento_para_o_lead_canonico_sem_carimbo()
    {
        using var db = CriarBanco();
        var repositorio = new ConversaRepositorio(db);
        var criadoEm = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);
        var conversaCanonica = await repositorio.ObterOuCriarAsync(Guid.NewGuid(), criadoEm, default);
        await repositorio.RegistrarContatoAsync(
            conversaCanonica,
            new ContatoRequest("Lead existente", "11999999999", null),
            criadoEm,
            default);

        var consentimentoEm = criadoEm.AddMinutes(5);
        var conversaComConsentimento = await repositorio.RegistrarConsentimentoAsync(
            Guid.NewGuid(),
            AvisoPrivacidade.VersaoAtual,
            consentimentoEm,
            default);
        var leadRemovidoId = conversaComConsentimento.LeadId;

        var leadMantidoId = await repositorio.RegistrarContatoAsync(
            conversaComConsentimento,
            new ContatoRequest("Lead existente", "11999999999", null),
            consentimentoEm.AddMinutes(1),
            default);

        var leadMantido = await db.Leads.SingleAsync(lead => lead.Id == leadMantidoId);

        Assert.Equal(conversaCanonica.LeadId, leadMantidoId);
        Assert.Equal(consentimentoEm, leadMantido.ConsentimentoEm);
        Assert.Equal(AvisoPrivacidade.VersaoAtual, leadMantido.VersaoAvisoPrivacidade);
        Assert.DoesNotContain(db.Leads, lead => lead.Id == leadRemovidoId);
    }

    [Fact]
    public void Dedupe_preserva_o_aceite_mais_recente_com_sua_versao()
    {
        var primeiroEm = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);
        var segundoEm = primeiroEm.AddMinutes(5);
        var canonico = Solar.Api.Dominio.Lead.Novo(primeiroEm);
        var outro = Solar.Api.Dominio.Lead.Novo(segundoEm);

        canonico.RegistrarConsentimento("2026-08-01", primeiroEm);
        outro.RegistrarConsentimento(AvisoPrivacidade.VersaoAtual, segundoEm);
        canonico.Absorver(outro, segundoEm.AddMinutes(1));

        Assert.Equal(segundoEm, canonico.ConsentimentoEm);
        Assert.Equal(AvisoPrivacidade.VersaoAtual, canonico.VersaoAvisoPrivacidade);
    }
}
