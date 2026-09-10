using System.Net.Sockets;
using Solar.Api.Persistencia;

namespace Solar.Api.Tests;

/// <summary>
/// "Banco indisponivel" e "migration errada" viravam a mesma linha de log, e a
/// segunda esperava cinco tentativas para aparecer. Aqui a distincao e teste.
/// </summary>
public class MigracaoDoBancoTeste
{
    [Fact]
    public void Falha_de_socket_e_indisponibilidade() =>
        Assert.True(MigracaoDoBanco.EhIndisponibilidade(new SocketException(10061)));

    [Fact]
    public void Timeout_e_indisponibilidade() =>
        Assert.True(MigracaoDoBanco.EhIndisponibilidade(new TimeoutException()));

    [Fact]
    public void Socket_embrulhado_continua_sendo_indisponibilidade() =>
        Assert.True(MigracaoDoBanco.EhIndisponibilidade(
            new InvalidOperationException("falhou ao abrir", new SocketException(10061))));

    [Fact]
    public void Erro_de_modelo_nao_e_indisponibilidade() =>
        Assert.False(MigracaoDoBanco.EhIndisponibilidade(
            new InvalidOperationException("The model for context 'SolarDbContext' changes each time it is built.")));

    [Fact]
    public void Erro_generico_nao_e_indisponibilidade() =>
        Assert.False(MigracaoDoBanco.EhIndisponibilidade(new Exception("qualquer outra coisa")));
}
