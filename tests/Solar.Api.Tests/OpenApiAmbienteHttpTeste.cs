using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class OpenApiAmbienteHttpTeste
{
    [Fact]
    public async Task OpenApi_e_Swagger_sao_restritos_ao_ambiente_de_desenvolvimento()
    {
        using var producao = new FabricaOpenApi(Environments.Production);
        using var clienteProducao = producao.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var caminho in new[] { "/openapi/v1.json", "/swagger", "/swagger/index.html" })
        {
            using var resposta = await clienteProducao.GetAsync(caminho);
            Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        }

        using var desenvolvimento = new FabricaOpenApi(Environments.Development);
        using var clienteDesenvolvimento = desenvolvimento.CreateClient();

        foreach (var caminho in new[] { "/openapi/v1.json", "/swagger", "/swagger/index.html" })
        {
            using var resposta = await clienteDesenvolvimento.GetAsync(caminho);
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        }
    }

    private sealed class FabricaOpenApi : WebApplicationFactory<Program>
    {
        private readonly string ambiente;
        private readonly string? conexaoAnterior =
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

        public FabricaOpenApi(string ambiente)
        {
            this.ambiente = ambiente;
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Postgres",
                PostgresTestDatabase.ObterConexaoParaAplicacao());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment(ambiente);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", conexaoAnterior);
            }

            base.Dispose(disposing);
        }
    }
}
