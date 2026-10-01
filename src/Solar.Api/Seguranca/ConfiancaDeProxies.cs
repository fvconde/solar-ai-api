using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using IpNetwork = System.Net.IPNetwork;

namespace Solar.Api.Seguranca;

/// <summary>
/// Lista fechada dos saltos que podem informar o IP original no header configurado.
/// </summary>
internal sealed class ConfiancaDeProxies
{
    internal const string SecaoConfiguracao = "ProxyTrust";
    private const int LimiteMaximoDeSaltos = 8;
    private const int QuantidadeMaximaDeProxies = 32;

    /// <summary>
    /// Cadeia: cliente -> Cloud Run solar-front (nginx e helper local de token) -> Cloud Run solar-api -> API.
    /// solar-api exige IAM (--no-allow-unauthenticated); somente a service account dedicada do solar-front
    /// recebe run.invoker. O helper obtem um ID token com audiencia do solar-api e o nginx sobrescreve
    /// X-Serverless-Authorization com ele. Cloud Run recusa acesso direto sem essa identidade antes de
    /// a API tratar headers; ranges compartilhados do Google nao substituem autenticacao do solicitante.
    /// Configure ProxyTrust:ForwardedForHeaderName, ForwardLimit, KnownProxies e KnownIPNetworks. O criterio 10 verifica
    /// os peers observados; nao invente nem fixe um IP de egress presumido do Cloud Run.
    ///
    /// O nginx normaliza a cadeia recebida do Cloud Run, remove qualquer prefixo X-Forwarded-For
    /// enviado pelo cliente e define X-Forwarded-For e X-Solar-Client-IP normalizados para a API. Nunca repasse cegamente
    /// $http_x_forwarded_for nem anexe entrada nao confiavel com $proxy_add_x_forwarded_for. Use um
    /// subrequest local de autenticacao; o helper renova o token e nunca o devolve ao navegador.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    public string ForwardedForHeaderName { get; set; } = "X-Forwarded-For";
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownIPNetworks { get; set; } = [];

    internal void Registrar(IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(Configurar);
        if (ForwardedForHeaderName == "X-Solar-Client-IP")
        {
            services.AddTransient<IStartupFilter, HeaderDeClienteUnicoStartupFilter>();
        }
    }

    internal void Configurar(ForwardedHeadersOptions options)
    {
        if (ForwardedForHeaderName is not ("X-Forwarded-For" or "X-Solar-Client-IP"))
        {
            throw new InvalidOperationException(
                "ProxyTrust:ForwardedForHeaderName deve ser X-Forwarded-For ou X-Solar-Client-IP.");
        }

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

        // Com ambas as listas vazias, o middleware deixa de validar KnownProxies/KnownIPNetworks.
        // Nesse estado, desative X-Forwarded-For para que nenhum peer arbitrário altere o IP remoto.
        options.ForwardedHeaders = KnownProxies.Length == 0 && KnownIPNetworks.Length == 0
            ? ForwardedHeaders.None
            : ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = ForwardLimit;
        options.ForwardedForHeaderName = ForwardedForHeaderName;

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

// X-Solar-Client-IP transporta um unico IP normalizado pelo front autenticado por IAM.
// O middleware padrao aceita listas; remova valores ambiguos antes dele, sem registrar valores.
internal sealed class HeaderDeClienteUnicoStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuar) =>
        {
            var valores = context.Request.Headers["X-Solar-Client-IP"];
            if (valores.Count != 0 &&
                (valores.Count != 1 || valores[0] is not { } valor ||
                 valor != valor.Trim() || !IPAddress.TryParse(valor, out _)))
            {
                context.Request.Headers.Remove("X-Solar-Client-IP");
            }

            await continuar(context);
        });
        next(app);
    };
}
