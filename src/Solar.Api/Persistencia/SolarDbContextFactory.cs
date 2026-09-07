using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Solar.Api.Persistencia;

/// <summary>
/// Constroi o contexto para o <c>dotnet ef</c>, que roda fora da aplicacao e nao
/// tem as variaveis de ambiente do compose.
///
/// <para>
/// A string aqui e so para o EF saber que o provider e o Postgres e conseguir
/// gerar o SQL da migration -- <c>migrations add</c> nao abre conexao. Quem
/// precisa de banco de verdade e o <c>database update</c>, e ai a
/// <c>ConnectionStrings__Postgres</c> do ambiente e usada.
/// </para>
/// </summary>
public sealed class SolarDbContextFactory : IDesignTimeDbContextFactory<SolarDbContext>
{
    private const string ConexaoDeProjeto = "Host=localhost;Port=5432;Database=solar;Username=solar";

    public SolarDbContext CreateDbContext(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? ConexaoDeProjeto;

        var opcoes = new DbContextOptionsBuilder<SolarDbContext>()
            .UseNpgsql(conexao)
            .Options;

        return new SolarDbContext(opcoes);
    }
}
