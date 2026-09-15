namespace Solar.Api.Dominio;

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

    public string Especialidade { get; private set; } = Especialidades.Moradia;

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

 public DateTimeOffset CriadoEm { get; private set; }

 public void DefinirCredenciais(string email, string emailNormalizado, string senhaHash)
 {
  Email = email;
  EmailNormalizado = emailNormalizado;
  SenhaHash = senhaHash;
 }

 public void DefinirSenhaHash(string senhaHash) => SenhaHash = senhaHash;

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
