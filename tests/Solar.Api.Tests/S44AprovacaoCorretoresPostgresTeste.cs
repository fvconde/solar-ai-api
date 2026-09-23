using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class S44AprovacaoCorretoresPostgresTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Listagem_de_pendentes_e_restrita_a_supervisor_e_ordenada()
    {
        var (_, supervisorEmail, supervisorSenha, _) = await CriarContaAsync(
            PerfisDoPainel.Supervisor, "supervisor-pendentes");
        var primeiro = await CriarContaAsync(PerfisDoPainel.Corretor, "pendente-primeiro");
        var segundo = await CriarContaAsync(PerfisDoPainel.Corretor, "pendente-segundo");
        await CriarContaAsync(PerfisDoPainel.Corretor, "aprovado-nao-pendente", aprovado: true);

        using var anonimo = factory.CreateClient();
        using var semSessao = await anonimo.GetAsync("/api/painel/corretores/pendentes");
        Assert.Equal(HttpStatusCode.Unauthorized, semSessao.StatusCode);

        var (_, corretorEmail, corretorSenha, _) = await CriarContaAsync(
            PerfisDoPainel.Corretor, "corretor-listagem-acesso", aprovado: true);
        using var corretor = await LoginAsync(corretorEmail, corretorSenha);
        using var proibido = await corretor.GetAsync("/api/painel/corretores/pendentes");
        Assert.Equal(HttpStatusCode.Forbidden, proibido.StatusCode);
        using var proibidoAprovar = await corretor.PostAsync(
            $"/api/painel/corretores/{primeiro.Id:D}/aprovacao", content: null);
        using var proibidoRecusar = await corretor.PostAsJsonAsync(
            $"/api/painel/corretores/{primeiro.Id:D}/recusa", new { motivo = "Não permitido" });
        Assert.Equal(HttpStatusCode.Forbidden, proibidoAprovar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, proibidoRecusar.StatusCode);

        using var supervisor = await LoginAsync(supervisorEmail, supervisorSenha);
        using var resposta = await supervisor.GetAsync("/api/painel/corretores/pendentes");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var pendentes = await resposta.Content.ReadFromJsonAsync<IReadOnlyList<CorretorPendente>>();
        Assert.NotNull(pendentes);
        var idsDoTeste = new[] { primeiro.Id, segundo.Id };
        var pendentesDoTeste = pendentes.Where(c => idsDoTeste.Contains(c.Id)).ToArray();
        var ordemEsperada = new[] { primeiro, segundo }
            .OrderBy(c => c.CriadoEm)
            .ThenBy(c => c.Id)
            .Select(c => c.Id);
        Assert.Equal(ordemEsperada, pendentesDoTeste.Select(c => c.Id));
    }

    [Fact]
    public async Task Corretor_em_analise_recebe_fila_vazia_e_detalhe_404_mesmo_com_encaminhamento_antigo()
    {
        var (corretorId, email, senha, _) = await CriarContaAsync(PerfisDoPainel.Corretor, "pendente-fila");
        var leadId = Guid.NewGuid();
        var conversaId = Guid.NewGuid();
        await using (var escopo = factory.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
            var conversa = Conversa.Nova(conversaId, Canais.Web, DateTimeOffset.UtcNow);
            db.Conversas.Add(conversa);
            db.Encaminhamentos.Add(Encaminhamento.Novo(
                conversa.Id, conversa.LeadId, corretorId, Especialidades.Moradia, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            leadId = conversa.LeadId;
        }

        using var corretor = await LoginAsync(email, senha);
        using var fila = await corretor.GetAsync("/api/painel/leads");
        Assert.Equal(HttpStatusCode.OK, fila.StatusCode);
        var itens = await fila.Content.ReadFromJsonAsync<FilaLeadsResponse>();
        Assert.NotNull(itens);
        Assert.Empty(itens.Itens);
        Assert.Equal(0, itens.Total);

        using var detalhe = await corretor.GetAsync($"/api/painel/leads/{leadId:D}");
        Assert.Equal(HttpStatusCode.NotFound, detalhe.StatusCode);
    }

    [Fact]
    public async Task Supervisor_aprova_corretor_em_analise_define_data_e_envia_email()
    {
        var emails = new EnviadorCapturado();
        using var host = HostComEmailCapturado(emails);
        var (supervisorId, supervisorEmail, supervisorSenha, _) = await CriarContaAsync(
            PerfisDoPainel.Supervisor, "supervisor-aprovar");
        var (corretorId, corretorEmail, _, _) = await CriarContaAsync(
            PerfisDoPainel.Corretor, "aprovar-destino");
        using var supervisor = await LoginAsync(host, supervisorEmail, supervisorSenha);

        using var aprovado = await supervisor.PostAsync(
            $"/api/painel/corretores/{corretorId:D}/aprovacao", content: null);
        Assert.Equal(HttpStatusCode.NoContent, aprovado.StatusCode);
        Assert.Equal(new MensagemEmail(corretorId, corretorEmail, "Conta aprovar-destino"),
            Assert.Single(emails.Aprovacoes));
        Assert.Equal(supervisorId, await IdDaSessaoAsync(supervisor));

        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var corretor = await db.Corretores.SingleAsync(c => c.Id == corretorId);
        Assert.Equal(StatusDoCorretor.Aprovado, corretor.StatusCorretor);
        Assert.NotNull(corretor.AprovadoEm);

        using var segundaTentativa = await supervisor.PostAsync(
            $"/api/painel/corretores/{corretorId:D}/aprovacao", content: null);
        Assert.Equal(HttpStatusCode.NotFound, segundaTentativa.StatusCode);
        Assert.Single(emails.Aprovacoes);
    }

    [Fact]
    public async Task Supervisor_recusa_corretor_apaga_conta_revoga_sessao_e_envia_motivo()
    {
        var emails = new EnviadorCapturado();
        using var host = HostComEmailCapturado(emails);
        var (_, supervisorEmail, supervisorSenha, _) = await CriarContaAsync(
            PerfisDoPainel.Supervisor, "supervisor-recusar");
        var (corretorId, corretorEmail, corretorSenha, _) = await CriarContaAsync(
            PerfisDoPainel.Corretor, "recusar-destino");
        using var corretorSessao = await LoginAsync(host, corretorEmail, corretorSenha);
        using var supervisor = await LoginAsync(host, supervisorEmail, supervisorSenha);

        using var recusado = await supervisor.PostAsJsonAsync(
            $"/api/painel/corretores/{corretorId:D}/recusa", new { motivo = "Documentação incompleta" });
        Assert.Equal(HttpStatusCode.NoContent, recusado.StatusCode);
        Assert.Equal(new MensagemEmail(corretorId, corretorEmail, "Conta recusar-destino", "Documentação incompleta"),
            Assert.Single(emails.Recusas));

        using var sessaoRevogada = await corretorSessao.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.Unauthorized, sessaoRevogada.StatusCode);
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        Assert.False(await db.Corretores.AnyAsync(c => c.Id == corretorId));
        Assert.False(await db.Sessoes.AnyAsync(s => s.CorretorId == corretorId));

        using var motivoLongo = await supervisor.PostAsJsonAsync(
            $"/api/painel/corretores/{Guid.NewGuid():D}/recusa", new { motivo = new string('x', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, motivoLongo.StatusCode);
    }

    private WebApplicationFactory<Program> HostComEmailCapturado(EnviadorCapturado emails) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEnviadorEmail>();
            services.AddSingleton<IEnviadorEmail>(emails);
        }));

    private async Task<(Guid Id, string Email, string Senha, DateTimeOffset CriadoEm)> CriarContaAsync(
        string perfil,
        string tag,
        bool aprovado = false)
    {
        const string senha = "Senha-segura-44";
        var email = $"{tag}-{Guid.NewGuid():N}@tests.solar.local";
        var criadoEm = DateTimeOffset.UtcNow;
        await using var escopo = factory.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<SolarDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<Corretor>>();
        var conta = Corretor.NovaConta(
            $"Conta {tag}", email, email, "11999998888", "placeholder", perfil,
            ["centro"], [Especialidades.Moradia], AvisoPrivacidade.VersaoAtual, criadoEm);
        conta.DefinirSenhaHash(hasher.HashPassword(conta, senha));
        if (aprovado) conta.Aprovar(criadoEm);
        db.Corretores.Add(conta);
        await db.SaveChangesAsync();
        return (conta.Id, email, senha, criadoEm);
    }

    private async Task<HttpClient> LoginAsync(string email, string senha) =>
        await LoginAsync(factory, email, senha);

    private static async Task<HttpClient> LoginAsync(
        WebApplicationFactory<Program> host,
        string email,
        string senha)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var resposta = await client.PostAsJsonAsync("/api/sessoes", new { email, senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", resposta.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
        return client;
    }

    private static async Task<Guid> IdDaSessaoAsync(HttpClient client)
    {
        using var resposta = await client.GetAsync("/api/sessao");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var sessao = await resposta.Content.ReadFromJsonAsync<SessaoResponse>();
        Assert.NotNull(sessao);
        return sessao.Usuario.Id;
    }

    private sealed record MensagemEmail(Guid ContaId, string Email, string Nome, string? Motivo = null);

    private sealed class EnviadorCapturado : IEnviadorEmail
    {
        public List<MensagemEmail> Aprovacoes { get; } = [];
        public List<MensagemEmail> Recusas { get; } = [];

        public Task EnviarLinkRecuperacaoAsync(
            string destinatario,
            string link,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnviarAprovacaoAsync(
            Guid contaId,
            string destinatario,
            string nome,
            CancellationToken cancellationToken = default)
        {
            Aprovacoes.Add(new MensagemEmail(contaId, destinatario, nome));
            return Task.CompletedTask;
        }

        public Task EnviarRecusaAsync(
            Guid contaId,
            string destinatario,
            string nome,
            string? motivo,
            CancellationToken cancellationToken = default)
        {
            Recusas.Add(new MensagemEmail(contaId, destinatario, nome, motivo));
            return Task.CompletedTask;
        }
    }
}
