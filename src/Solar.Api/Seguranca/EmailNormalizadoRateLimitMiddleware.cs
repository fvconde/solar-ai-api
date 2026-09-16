using System.Text.Json;

namespace Solar.Api.Seguranca;

/// <summary>
/// O middleware de rate limit executa antes do model binding. Este pequeno
/// passo apenas extrai o e-mail do JSON para que a politica "painel" possa
/// particionar recuperacoes pelo valor normalizado sem guardar o corpo.
/// </summary>
public sealed class EmailNormalizadoRateLimitMiddleware(RequestDelegate next)
{
    public const string ItemEmailNormalizado = "Solar.Painel.EmailNormalizado";
    private const long LimiteCorpo = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method.Equals(HttpMethods.Post, StringComparison.OrdinalIgnoreCase) &&
            (context.Request.Path.Equals("/painel/identificacao") ||
             context.Request.Path.Equals("/painel/senha/recuperacoes")) &&
            (context.Request.ContentLength is > 0 && context.Request.ContentLength <= LimiteCorpo))
        {
            context.Request.EnableBuffering();

            using var reader = new StreamReader(
                context.Request.Body,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var corpo = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;

            try
            {
                using var json = JsonDocument.Parse(corpo);

                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("email", out var email) &&
                    email.ValueKind == JsonValueKind.String &&
                    NormalizadorDeEmail.TentarNormalizar(email.GetString(), out var normalizado))
                {
                    context.Items[ItemEmailNormalizado] = normalizado;
                }
            }
            catch (JsonException)
            {
                // O controller continua responsavel pela resposta de formato.
            }
        }

        await next(context);
    }
}
