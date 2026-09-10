using System.Text.RegularExpressions;
using Solar.Api.Contracts;

namespace Solar.Api.Dominio;

/// <summary>
/// Normaliza o que o lead digita antes de gravar, para que o mesmo contato
/// escrito de dois jeitos caia no mesmo registro.
/// </summary>
public static partial class Contato
{
    public const int LimiteTelefone = ContratoContato.LimiteTelefone;
    public const int LimiteEmail = ContratoContato.LimiteEmail;

    public static string? Telefone(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto))
        {
            return null;
        }

        var digitos = SomenteDigitos().Replace(bruto, string.Empty);

        return digitos.Length == 0 ? null : digitos;
    }

    public static string? Email(string? bruto) =>
        string.IsNullOrWhiteSpace(bruto) ? null : bruto.Trim().ToLowerInvariant();

    public static string? Nome(string? bruto) =>
        string.IsNullOrWhiteSpace(bruto) ? null : bruto.Trim();

    [GeneratedRegex(@"\D")]
    private static partial Regex SomenteDigitos();
}
