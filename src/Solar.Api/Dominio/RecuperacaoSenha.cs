namespace Solar.Api.Dominio;

/// <summary>
/// Desafio de redefinicao de senha. O token opaco nao e persistido; apenas o
/// digest SHA-256 permite localizar e consumir o desafio uma unica vez.
/// </summary>
public sealed class RecuperacaoSenha
{
 private RecuperacaoSenha()
 {
 }

 public Guid Id { get; private set; }

 public Guid CorretorId { get; private set; }

 public Corretor? Corretor { get; private set; }

 public byte[] TokenHash { get; private set; } = [];

 public DateTimeOffset CriadaEm { get; private set; }

 public DateTimeOffset ExpiraEm { get; private set; }

 public DateTimeOffset? UsadaEm { get; private set; }

 public DateTimeOffset? InvalidadaEm { get; private set; }

 public static RecuperacaoSenha Nova(
  Guid corretorId,
  byte[] tokenHash,
  DateTimeOffset criadaEm,
  DateTimeOffset expiraEm) => new()
 {
  Id = Guid.NewGuid(),
  CorretorId = corretorId,
  TokenHash = tokenHash,
  CriadaEm = criadaEm,
  ExpiraEm = expiraEm,
 };

 public bool ValidaEm(DateTimeOffset agora) =>
  UsadaEm is null && InvalidadaEm is null && ExpiraEm > agora;

 public void Invalidar(DateTimeOffset em) => InvalidadaEm ??= em;

 public void Consumir(DateTimeOffset em) => UsadaEm ??= em;
}
