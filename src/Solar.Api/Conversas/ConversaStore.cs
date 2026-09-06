using System.Collections.Concurrent;

namespace Solar.Api.Conversas;

/// <summary>
/// Guarda as conversas em memoria ate o S-10 trocar por EF Core e Postgres.
/// Some quando o processo reinicia -- e o que se quer nesta fase.
/// </summary>
public sealed class ConversaStore
{
    private readonly ConcurrentDictionary<Guid, Conversa> _conversas = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _travas = new();

    public Conversa ObterOuCriar(Guid id) =>
        _conversas.GetOrAdd(id, chave => new Conversa(chave));

    public Conversa? Obter(Guid id) =>
        _conversas.TryGetValue(id, out var conversa) ? conversa : null;

    /// <summary>
    /// Serializa os turnos de uma mesma conversa. Duas mensagens simultaneas na
    /// mesma conversa produziriam historico intercalado e perfil corrompido.
    /// </summary>
    public async Task<IDisposable> TravarAsync(Guid id, CancellationToken cancellationToken)
    {
        var trava = _travas.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

        await trava.WaitAsync(cancellationToken);

        return new Liberacao(trava);
    }

    private sealed class Liberacao(SemaphoreSlim trava) : IDisposable
    {
        private int _liberado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                trava.Release();
            }
        }
    }
}
