using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Solar.Api.Contracts;
using Solar.Api.Dominio;

namespace Solar.Api.Encaminhamentos;

/// <summary>Um corretor e a carga que ele ja carrega, no formato que a escolha le.</summary>
public sealed record CorretorCandidato(
    Guid Id,
    string Especialidade,
    IReadOnlyList<string> Regioes,
    bool Ativo,
    int CargaAberta,
    DateTimeOffset? UltimoEncaminhamentoEm,
    DateTimeOffset CriadoEm);

/// <summary>
/// Decide quem assume o lead. Puro e deterministico: mesma entrada, mesma saida,
/// sem banco e sem relogio. E o que torna a regra explicavel em uma frase e
/// testavel sem rede.
/// </summary>
public static partial class EscolhaDeCorretor
{
    public static string EspecialidadeDe(string? intencao) =>
        intencao == Intencoes.Investimento ? Especialidades.Investimento : Especialidades.Moradia;

    public static Guid? Escolher(
        string? intencao,
        string? regiao,
        IReadOnlyList<CorretorCandidato> candidatos)
    {
        var especialidade = EspecialidadeDe(intencao);

        return candidatos
            .Where(candidato => candidato.Ativo)
            .Where(candidato => candidato.Especialidade == especialidade)
            .Where(candidato => Cobre(candidato.Regioes, regiao))
            .OrderBy(candidato => candidato.CargaAberta)
            .ThenBy(candidato => candidato.UltimoEncaminhamentoEm ?? DateTimeOffset.MinValue)
            .ThenBy(candidato => candidato.CriadoEm)
            .ThenBy(candidato => candidato.Id)
            .Select(candidato => (Guid?)candidato.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Espelha o <c>_regiao_bate</c> do indice do agente: termo de uma palavra so
    /// casa como palavra inteira -- senao "sul" acharia "insulado" --, termo com
    /// espaco casa como trecho. Regiao vazia nao exclui ninguem: nao saber onde o
    /// lead quer morar nao e o mesmo que saber que ninguem atende ali.
    /// </summary>
    public static bool Cobre(IReadOnlyList<string> regioes, string? regiao)
    {
        if (string.IsNullOrWhiteSpace(regiao))
        {
            return true;
        }

        var alvo = SemAcento(regiao);
        var palavras = Palavras().Matches(alvo).Select(palavra => palavra.Value).ToHashSet();

        foreach (var termo in regioes)
        {
            var limpo = SemAcento(termo);

            if (limpo.Length == 0)
            {
                continue;
            }

            if (limpo.Contains(' ') ? alvo.Contains(limpo) : palavras.Contains(limpo))
            {
                return true;
            }
        }

        return false;
    }

    private static string SemAcento(string texto)
    {
        var decomposto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var construtor = new StringBuilder(decomposto.Length);

        foreach (var caractere in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caractere) != UnicodeCategory.NonSpacingMark)
            {
                construtor.Append(caractere);
            }
        }

        return construtor.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Palavras();
}
