using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using IpNetwork = System.Net.IPNetwork;

namespace Solar.Api.Seguranca;

/// <summary>
/// Lista fechada dos saltos que podem informar o IP original em X-Forwarded-For.
/// </summary>
internal sealed class ConfiancaDeProxies
{
    internal const string SecaoConfiguracao = "ProxyTrust";
    private const int LimiteMaximoDeSaltos = 8;
    private const int QuantidadeMaximaDeProxies = 32;

    /// <summary>
    /// Cadeia esperada: cliente -> Cloud Run solar-front -> nginx -> Cloud Run solar-api -> API.
    /// Cloud Run não oferece um IP de saída fixo por padrão; por isso a lista base fica vazia.
    /// Só devem ser configurados os peers observados e validados para o caminho solar-front -> solar-api.
    /// No deploy, use apenas a seção ProxyTrust: ForwardLimit, KnownProxies e KnownIPNetworks.
    /// Não inclua ranges compartilhados do Google se o API continuar acessível diretamente: nesse caso,
    /// o caminho direto e o caminho do nginx podem chegar pelo mesmo proxy de borda. O critério 10 deve
    /// verificar a cadeia observada e, se necessário, exigir ingress interno ou egress estático exclusivo.
    ///
    /// O nginx deve determinar o cliente pela parte confiável da cadeia de entrada do Cloud Run, remover
    /// qualquer prefixo X-Forwarded-For recebido do cliente e encaminhar somente a cadeia normalizada.
    /// Não encaminhe cegamente $http_x_forwarded_for nem acrescente a ele $proxy_add_x_forwarded_for.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownIPNetworks { get; set; } = [];

    internal void Configurar(ForwardedHeadersOptions options)
    {
        if (ForwardLimit is < 1 or > LimiteMaximoDeSaltos)
        {
            throw new InvalidOperationException(
                $"ProxyTrust:ForwardLimit deve estar entre 1 e {LimiteMaximoDeSaltos}.");
        }

        if (KnownProxies.Length + KnownIPNetworks.Length > QuantidadeMaximaDeProxies)
        {
            throw new InvalidOperationException(
                $"ProxyTrust aceita no maximo {QuantidadeMaximaDeProxies} proxies ou redes.");
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = ForwardLimit;

        // Substitui até os defaults de loopback do ASP.NET. Sem configuração explícita, nenhum
        // peer é confiável e X-Forwarded-For é ignorado.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var valor in KnownProxies)
        {
            if (!IPAddress.TryParse(valor, out var endereco)
                || endereco.Equals(IPAddress.Any)
                || endereco.Equals(IPAddress.IPv6Any))
            {
                throw new InvalidOperationException(
                    "ProxyTrust:KnownProxies deve conter enderecos IP unicos e validos.");
            }

            options.KnownProxies.Add(endereco);
        }

        foreach (var valor in KnownIPNetworks)
        {
            if (!IpNetwork.TryParse(valor, out var rede) || rede.PrefixLength == 0)
            {
                throw new InvalidOperationException(
                    "ProxyTrust:KnownIPNetworks deve conter CIDRs validos e nunca uma rede /0.");
            }

            options.KnownIPNetworks.Add(rede);
        }
    }
}
