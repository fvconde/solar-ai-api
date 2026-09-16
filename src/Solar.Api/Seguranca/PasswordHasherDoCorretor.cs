using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Solar.Api.Dominio;

namespace Solar.Api.Seguranca;

public static class PasswordHasherDoCorretor
{
    public const int IterationCount = 220_000;

    public static PasswordHasher<Corretor> Criar() =>
        new(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = IterationCount,
        }));
}
