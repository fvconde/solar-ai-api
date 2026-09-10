using System.Reflection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;

namespace Solar.Api.Tests;

public class LeadTeste
{
    private static readonly DateTimeOffset Agora = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static CamposExtraidos Nada => new();

    [Fact]
    public void Fundir_nao_apaga_o_que_ja_se_sabia()
    {
        var lead = Lead.Novo(Agora);

        lead.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Ana", Regiao: "zona sul"), Agora);
        lead.Fundir(Intencoes.Compra, new CamposExtraidos(Quartos: 2), Agora);

        Assert.Equal("Ana", lead.Nome);
        Assert.Equal("zona sul", lead.Regiao);
        Assert.Equal(2, lead.Quartos);
    }

    [Fact]
    public void Fundir_com_intencao_indefinida_preserva_a_intencao_conhecida()
    {
        var lead = Lead.Novo(Agora);

        lead.Fundir(Intencoes.Investimento, Nada, Agora);
        lead.Fundir(Intencoes.Indefinida, Nada, Agora);

        Assert.Equal(Intencoes.Investimento, lead.Intencao);
    }

    [Fact]
    public void Lead_nasce_novo_e_sem_contato()
    {
        var lead = Lead.Novo(Agora);

        Assert.Equal(StatusDoLead.Novo, lead.Status);
        Assert.False(lead.TemContato);
    }

    [Fact]
    public void Registrar_contato_normaliza_e_marca_que_ha_contato()
    {
        var lead = Lead.Novo(Agora);

        lead.RegistrarContato("  Ana  ", "(11) 99999-8888", "  ANA@Solar.Local ", Agora);

        Assert.Equal("Ana", lead.Nome);
        Assert.Equal("11999998888", lead.Telefone);
        Assert.Equal("ana@solar.local", lead.Email);
        Assert.True(lead.TemContato);
    }

    [Fact]
    public void Registrar_contato_so_com_email_ja_conta_como_contato()
    {
        var lead = Lead.Novo(Agora);

        lead.RegistrarContato("Ana", null, "ana@solar.local", Agora);

        Assert.True(lead.TemContato);
        Assert.Null(lead.Telefone);
    }

    [Fact]
    public void Absorver_traz_o_que_a_conversa_deduplicada_sabia()
    {
        var canonico = Lead.Novo(Agora);
        canonico.Fundir(Intencoes.Compra, new CamposExtraidos(Nome: "Ana", Quartos: 2), Agora);

        var orfao = Lead.Novo(Agora);
        orfao.Fundir(Intencoes.Investimento, new CamposExtraidos(Regiao: "Moema", Score: 80), Agora);

        canonico.Absorver(orfao, Agora);

        Assert.Equal("Moema", canonico.Regiao);
        Assert.Equal(80, canonico.Score);
        Assert.Equal(Intencoes.Investimento, canonico.Intencao);
        Assert.Equal(2, canonico.Quartos);
        Assert.Equal("Ana", canonico.Nome);
    }

    [Fact]
    public void Marcar_encaminhado_muda_o_status()
    {
        var lead = Lead.Novo(Agora);

        lead.MarcarEncaminhado(Agora);

        Assert.Equal(StatusDoLead.Encaminhado, lead.Status);
    }
}

/// <summary>
/// O telefone nao vai ao modelo porque o contrato do turno nao tem campo para
/// ele. Restricao que nao pode ser violada e codigo, e aqui ela e teste.
/// </summary>
public class ContratoDoTurnoTeste
{
    private static readonly Type[] Espelho =
    [
        typeof(MensagemHistorico),
        typeof(PerfilLead),
        typeof(CamposExtraidos),
        typeof(ImovelSugerido),
        typeof(TurnoRequest),
        typeof(TurnoResponse),
    ];

    private static PropertyInfo[] Campos(Type tipo) =>
        tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void Nenhum_tipo_do_espelho_carrega_contato()
    {
        var proibidos = new[] { "telefone", "celular", "email", "whatsapp", "contato" };

        foreach (var tipo in Espelho)
        {
            var campos = Campos(tipo).Select(campo => campo.Name.ToLowerInvariant()).ToArray();

            Assert.DoesNotContain(campos, campo => proibidos.Any(campo.Contains));
        }
    }

    [Fact]
    public void Espelho_continua_com_6_tipos_e_37_campos()
    {
        Assert.Equal(6, Espelho.Length);
        Assert.Equal(37, Espelho.Sum(tipo => Campos(tipo).Length));
    }
}
