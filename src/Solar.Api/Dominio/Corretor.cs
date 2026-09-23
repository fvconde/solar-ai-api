using System.ComponentModel.DataAnnotations.Schema;

namespace Solar.Api.Dominio;

public static class PerfisDoPainel
{
    public const string Cliente = "cliente";
    public const string Corretor = "corretor";
    public const string Supervisor = "supervisor";
}

public static class StatusDoCorretor
{
    public const string EmAnalise = "em_analise";
    public const string Aprovado = "aprovado";
}

public static class Especialidades
{
    public const string Moradia = "moradia";
    public const string Investimento = "investimento";
}

/// <summary>
/// O corretor humano que assume o lead depois do encaminhamento. A base e
/// semeada por migration: nao ha tela de gestao.
/// </summary>
public sealed class Corretor
{
    private Corretor()
    {
    }

    public Guid Id { get; private set; }

    public string Nome { get; private set; } = string.Empty;

    public List<string> Especialidades { get; private set; } = [];

    // Compatibilidade interna com consumidores do painel anteriores ao S-44.
    [NotMapped]
    public string Especialidade
    {
        get => Especialidades.FirstOrDefault() ?? Solar.Api.Dominio.Especialidades.Moradia;
        private set => Especialidades = string.IsNullOrWhiteSpace(value) ? [] : [value];
    }

    public string Perfil { get; private set; } = PerfisDoPainel.Corretor;

    public bool VinculoAtivo { get; private set; } = true;

    /// <summary>Termos de cobertura -- zonas ou bairros -- casados contra a regiao do lead.</summary>
    public List<string> Regioes { get; private set; } = [];

 public string ContatoInterno { get; private set; } = string.Empty;

 /// <summary>
 /// E-mail usado pelo corretor para entrar no painel. O valor exibido e
 /// preservado para a interface; consultas de autenticacao usam
 /// <see cref="EmailNormalizado"/>.
 /// </summary>
 public string Email { get; private set; } = string.Empty;

 public string EmailNormalizado { get; private set; } = string.Empty;

 /// <summary>
 /// Hash produzido por <c>PasswordHasher&lt;Corretor&gt;</c>. A senha em claro
 /// nunca pertence ao dominio nem ao estado persistido.
 /// </summary>
 public string? SenhaHash { get; private set; }

 public int TentativasSenha { get; private set; }

 public DateTimeOffset? BloqueadoAte { get; private set; }

    public bool Ativo { get; private set; }

    public string? Telefone { get; private set; }

    public string? StatusCorretor { get; private set; }

    public DateTimeOffset? AprovadoEm { get; private set; }

    public DateTimeOffset? ConsentimentoEm { get; private set; }

    public string? VersaoAvisoPrivacidade { get; private set; }

 public DateTimeOffset CriadoEm { get; private set; }

 public void DefinirCredenciais(string email, string emailNormalizado, string senhaHash)
 {
  Email = email;
  EmailNormalizado = emailNormalizado;
  SenhaHash = senhaHash;
 }

 public void DefinirSenhaHash(string senhaHash) => SenhaHash = senhaHash;

 public static Corretor NovaConta(
     string nome,
     string email,
     string emailNormalizado,
     string telefone,
     string senhaHash,
     string perfil,
     IReadOnlyList<string> regioes,
     IReadOnlyList<string> especialidades,
     string versaoAvisoPrivacidade,
     DateTimeOffset em) => new()
 {
     Id = Guid.NewGuid(),
     Nome = nome,
     Email = email,
     EmailNormalizado = emailNormalizado,
     Telefone = telefone,
     SenhaHash = senhaHash,
     Perfil = perfil,
     Regioes = [.. regioes],
     Especialidades = [.. especialidades],
     StatusCorretor = perfil == PerfisDoPainel.Cliente ? null : StatusDoCorretor.EmAnalise,
     Ativo = true,
     VinculoAtivo = true,
     CriadoEm = em,
     ConsentimentoEm = em,
     VersaoAvisoPrivacidade = versaoAvisoPrivacidade,
 };

 public void AtualizarNome(string nome) => Nome = nome;

 public void AtualizarEmail(string email, string emailNormalizado)
 {
     Email = email;
     EmailNormalizado = emailNormalizado;
 }

 public void AtualizarTelefone(string telefone) => Telefone = telefone;

 public void AtualizarAtuacao(IReadOnlyList<string> regioes, IReadOnlyList<string> especialidades)
 {
     Regioes = [.. regioes];
     Especialidades = [.. especialidades];
 }

 public void Aprovar(DateTimeOffset em)
 {
     StatusCorretor = StatusDoCorretor.Aprovado;
     AprovadoEm = em;
 }

 public void Desativar() => Ativo = false;

 public void RegistrarFalhaDeSenha(DateTimeOffset agora, TimeSpan duracaoBloqueio)
 {
  TentativasSenha = Math.Min(TentativasSenha + 1, 5);

  if (TentativasSenha >= 5)
  {
   BloqueadoAte = agora.Add(duracaoBloqueio);
  }
 }

 public void RegistrarAcertoDeSenha()
 {
  TentativasSenha = 0;
  BloqueadoAte = null;
 }

 public void LimparBloqueioExpirado()
 {
  TentativasSenha = 0;
  BloqueadoAte = null;
 }
}
