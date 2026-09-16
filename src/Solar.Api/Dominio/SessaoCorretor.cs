namespace Solar.Api.Dominio;

/// <summary>
/// Sessao persistente por dispositivo. O valor recebido no cookie nunca e
/// armazenado: somente seu SHA-256 fica nesta entidade.
/// </summary>
public sealed class SessaoCorretor
{
 private SessaoCorretor()
 {
 }

 public Guid Id { get; private set; }

 public Guid CorretorId { get; private set; }

 public Corretor? Corretor { get; private set; }

 public byte[] TokenHash { get; private set; } = [];

 public DateTimeOffset CriadaEm { get; private set; }

 public DateTimeOffset? RevogadaEm { get; private set; }

 public static SessaoCorretor Nova(Guid corretorId, byte[] tokenHash, DateTimeOffset criadaEm) => new()
 {
  Id = Guid.NewGuid(),
  CorretorId = corretorId,
  TokenHash = tokenHash,
  CriadaEm = criadaEm,
 };

 public void Revogar(DateTimeOffset em) => RevogadaEm ??= em;
}
