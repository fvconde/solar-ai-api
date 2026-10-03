using Solar.Api.Dominio;

namespace Solar.Api.Seguranca;

public sealed class ProvaExclusaoTitular(Guid? contaId, byte[]? chaveHash)
{
    public Guid? ContaId => contaId;

    public bool Autoriza(Conversa conversa) => conversa.ContaId is { } dona
        ? contaId == dona
        : chaveHash is not null && TokenSeguro.HashesIguais(conversa.ChaveExclusaoHash, chaveHash);
}
