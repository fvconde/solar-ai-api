using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;

namespace Solar.Api.Servicos;

public sealed class SenhaInicialSupervisor(
    SolarDbContext db,
    IPasswordHasher<Corretor> passwordHasher,
    IConfiguration configuracao)
{
    public const string ChaveConfiguracao = "Semente:SenhaSupervisor";
    private static readonly Guid SupervisorId = new("3f6b9c21-4d0a-4c7e-9a11-000000000101");

    public async Task GarantirAsync(CancellationToken cancellationToken = default)
    {
        var senha = configuracao[ChaveConfiguracao];
        if (string.IsNullOrWhiteSpace(senha))
            return;

        var candidatos = db.Corretores.Where(c =>
            c.Id == SupervisorId &&
            c.Perfil == PerfisDoPainel.Supervisor &&
            c.VinculoAtivo &&
            c.SenhaHash == null);

        var supervisor = await candidatos.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (supervisor is null)
            return;

        var hash = passwordHasher.HashPassword(supervisor, senha);
        // Reavalia a ausencia de hash na gravacao, inclusive se outro boot o definiu.
        await candidatos.ExecuteUpdateAsync(
            campos => campos.SetProperty(c => c.SenhaHash, hash), cancellationToken);
    }
}
