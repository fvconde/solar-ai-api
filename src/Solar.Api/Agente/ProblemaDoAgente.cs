using Microsoft.AspNetCore.Mvc;

namespace Solar.Api.Agente;

public static class ProblemaDoAgente
{
    public static ObjectResult Traduzir(
        this ControllerBase controller,
        AgenteIndisponivelException erro,
        IHostEnvironment environment) =>
        controller.Problem(
            statusCode: erro.TempoEsgotado
                ? StatusCodes.Status504GatewayTimeout
                : StatusCodes.Status502BadGateway,
            title: "agente indisponivel",
            detail: environment.IsDevelopment() ? erro.Message : null);
}
