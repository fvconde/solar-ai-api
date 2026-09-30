using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solar.Api.Contracts;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class LimiteDeCadastrosHttpTeste(PainelApiFactory factory) : IClassFixture<PainelApiFactory>
{
    [Fact]
    public async Task Cadastro_e_limitado_por_ip()
    {
        using var client = factory.CreateClient();
        for (var i = 0; i < 20; i++)
        {
            using var resposta = await client.PostAsJsonAsync("/api/contas", new { });
            Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        }

        using var limitado = await client.PostAsJsonAsync("/api/contas", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, limitado.StatusCode);
    }
}
