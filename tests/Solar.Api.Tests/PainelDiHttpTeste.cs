using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Solar.Api.Contracts;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class PainelDiHttpTeste : IClassFixture<PainelApiFactory>
{
    private readonly HttpClient client;

    public PainelDiHttpTeste(PainelApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Identificacao_http_resolve_controller_pelo_grafo_real_de_DI()
    {
        using var resposta = await client.PostAsJsonAsync(
            "/painel/identificacao",
            new { email = $"di-{Guid.NewGuid():N}@tests.solar.local" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var contrato = await resposta.Content.ReadFromJsonAsync<IdentificacaoPainelResponse>();
        Assert.NotNull(contrato);
        Assert.False(contrato.Cadastrado);
    }
}

public sealed class PainelApiFactory : WebApplicationFactory<Program>
{
    private readonly string? connectionStringAnterior =
        Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

    public PainelApiFactory()
    {
        // O minimal hosting executa o entrypoint antes de aplicar o callback
        // tardio do WebApplicationFactory; a API exige a string no proprio
        // Program. O valor fica somente no processo dos testes.
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres",
            PostgresTestDatabase.ObterConexaoParaAplicacao());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Postgres",
                connectionStringAnterior);
        }

        base.Dispose(disposing);
    }
}
