using System.Collections.Concurrent;

namespace Solar.Api.Seguranca;

public interface IPainelRateLimitStore
{
    bool TentarConsumir(string particao, int limite, TimeSpan janela, DateTimeOffset agora);
}

/// <summary>
/// Contadores curtos para a particao por e-mail normalizado. A particao por IP
/// continua sendo aplicada pela politica nativa "painel" no middleware; este
/// armazenamento complementa a regra de e-mail dos pedidos de recuperacao.
/// </summary>
public sealed class PainelRateLimitStore : IPainelRateLimitStore
{
    private sealed class Janela(DateTimeOffset inicio)
    {
        public DateTimeOffset Inicio { get; set; } = inicio;
        public int Usos { get; set; }
    }

    private readonly ConcurrentDictionary<string, Janela> janelas = new(StringComparer.Ordinal);

    public bool TentarConsumir(string particao, int limite, TimeSpan janela, DateTimeOffset agora)
    {
        if (limite <= 0 || janela <= TimeSpan.Zero)
        {
            return false;
        }

        var contador = janelas.GetOrAdd(particao, _ => new Janela(agora));

        lock (contador)
        {
            if (agora < contador.Inicio || agora - contador.Inicio >= janela)
            {
                contador.Inicio = agora;
                contador.Usos = 0;
            }

            if (contador.Usos >= limite)
            {
                return false;
            }

            contador.Usos++;
            return true;
        }
    }
}
