using System.Security.Cryptography;
using System.Text;

namespace Solar.Api.Seguranca;

public static class TokenSeguro
{
    public const int TamanhoEmBytes = 32;

    public static string Criar()
    {
        Span<byte> bytes = stackalloc byte[TamanhoEmBytes];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static byte[] Sha256(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static bool HashesIguais(byte[]? armazenado, byte[] candidato) =>
        armazenado is not null &&
        armazenado.Length == candidato.Length &&
        CryptographicOperations.FixedTimeEquals(armazenado, candidato);

    public static byte[]? TentarCalcularSha256(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512)
        {
            return null;
        }

        return Sha256(token);
    }
}
