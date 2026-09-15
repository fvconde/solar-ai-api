using System.Text.RegularExpressions;

namespace Solar.Api.Seguranca;

public static partial class NormalizadorDeEmail
{
    private const int LimiteEmail = 320;

    public static bool TentarNormalizar(string? email, out string normalizado)
    {
        normalizado = string.Empty;

        if (email is null)
        {
            return false;
        }

        var aparado = email.Trim();

        if (aparado.Length is 0 or > LimiteEmail || !Formato().IsMatch(aparado))
        {
            return false;
        }

        normalizado = aparado.ToLowerInvariant();
        return true;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Formato();
}
