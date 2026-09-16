namespace Solar.Api.Conversas;

/// <summary>
/// Serializa os turnos de uma mesma conversa. Sem isso, duas mensagens
/// simultaneas leem o mesmo historico e uma das duas atualizacoes de perfil se
/// perde -- o banco nao resolve isso sozinho, porque a leitura, a chamada ao
/// agente e a gravacao sao tres passos separados por ate 45 segundos.
///
/// <para>
/// A trava vale <b>dentro de um processo</b>. Com mais de uma instancia da API
/// (S-26) ela deixa de proteger; o substituto e um <c>pg_advisory_xact_lock</c>
/// no proprio Postgres. Ate la o deploy roda com instancia unica.
/// </para>
///
/// <para>
/// Cada conversa ganha um semaforo sob demanda e o perde quando o ultimo
/// interessado sai. Manter o dicionario crescendo era inofensivo enquanto a
/// conversa morria com o processo; com Postgres, o numero de conversas nao para
/// mais de crescer.
/// </para>
/// </summary>
public sealed class TravaDeConversas
{
    private readonly Dictionary<Guid, Entrada> _entradas = [];

    public async Task<IDisposable> TravarAsync(Guid id, CancellationToken cancellationToken)
    {
        Entrada entrada;

        lock (_entradas)
        {
            if (!_entradas.TryGetValue(id, out entrada!))
            {
                entrada = new Entrada();
                _entradas[id] = entrada;
            }

            entrada.Interessados++;
        }

        try
        {
            await entrada.Semaforo.WaitAsync(cancellationToken);
        }
        catch
        {
            Soltar(id, entrada, liberarSemaforo: false);

            throw;
        }

        return new Liberacao(() => Soltar(id, entrada, liberarSemaforo: true));
    }

    /// <summary>
    /// Adquire a trava de multiplos identificadores (por exemplo, todas as conversas
    /// de um mesmo lead e o proprio lead) em ordem estavel (ordenada por GUID) para
    /// evitar deadlocks entre exclusao e turnos concorrentes.
    /// </summary>
    public async Task<IDisposable> TravarMultiplasAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var listaOrdenada = ids.Distinct().OrderBy(g => g).ToList();

        if (listaOrdenada.Count == 0)
        {
            return new Liberacao(() => { });
        }

        if (listaOrdenada.Count == 1)
        {
            return await TravarAsync(listaOrdenada[0], cancellationToken);
        }

        var liberacoes = new List<IDisposable>(listaOrdenada.Count);

        try
        {
            foreach (var id in listaOrdenada)
            {
                liberacoes.Add(await TravarAsync(id, cancellationToken));
            }

            return new LiberacaoMultipla(liberacoes);
        }
        catch
        {
            for (var i = liberacoes.Count - 1; i >= 0; i--)
            {
                liberacoes[i].Dispose();
            }

            throw;
        }
    }

    private void Soltar(Guid id, Entrada entrada, bool liberarSemaforo)
    {
        if (liberarSemaforo)
        {
            entrada.Semaforo.Release();
        }

        lock (_entradas)
        {
            if (--entrada.Interessados == 0)
            {
                _entradas.Remove(id);
                entrada.Semaforo.Dispose();
            }
        }
    }

    private sealed class Entrada
    {
        public SemaphoreSlim Semaforo { get; } = new(1, 1);

        public int Interessados { get; set; }
    }

    private sealed class Liberacao(Action soltar) : IDisposable
    {
        private int _liberado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                soltar();
            }
        }
    }

    private sealed class LiberacaoMultipla(List<IDisposable> liberacoes) : IDisposable
    {
        private int _liberado;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                for (var i = liberacoes.Count - 1; i >= 0; i--)
                {
                    liberacoes[i].Dispose();
                }
            }
        }
    }
}
