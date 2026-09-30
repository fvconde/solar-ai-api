using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Solar.Api.Dominio;
using Solar.Api.Persistencia;
using Solar.Api.Seguranca;
using Solar.Api.Servicos;

namespace Solar.Api.Tests;

[Collection(PostgresTestDatabase.CollectionName)]
public sealed class SenhaInicialSupervisorPostgresTeste
{
    private static readonly Guid SupervisorId = new("3f6b9c21-4d0a-4c7e-9a11-000000000101");
    private const string SenhaTeste = "Senha-ficticia-inicial!26";

    [Fact]
    public async Task Inicializa_supervisor_sem_hash_e_preserva_outras_contas()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await using var transacao = await db.Database.BeginTransactionAsync();
        await LimparHashAsync(db);
        var outrasAntes = await db.Corretores.AsNoTracking()
            .Where(c => c.Id != SupervisorId).ToDictionaryAsync(c => c.Id, c => c.SenhaHash);
        Assert.NotEmpty(outrasAntes);
        var antes = await SupervisorAsync(db);
        Assert.Equal("supervisor.vinculado@solar.local", antes.Email);
        var hasher = PasswordHasherDoCorretor.Criar();

        await Criar(db, SenhaTeste, hasher).GarantirAsync();

        var depois = await SupervisorAsync(db);
        Assert.True(depois.SenhaHash is not null, "Senha inicial nao foi definida.");
        Assert.Equal(PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(depois, depois.SenhaHash!, SenhaTeste));
        Assert.Equal(antes.Email, depois.Email);
        Assert.Equal(antes.Nome, depois.Nome);
        Assert.Equal(antes.Perfil, depois.Perfil);
        Assert.Equal(antes.VinculoAtivo, depois.VinculoAtivo);
        var outrasDepois = await db.Corretores.AsNoTracking()
            .Where(c => c.Id != SupervisorId).ToDictionaryAsync(c => c.Id, c => c.SenhaHash);
        Assert.True(outrasAntes.Count == outrasDepois.Count && outrasAntes.All(par =>
            outrasDepois.TryGetValue(par.Key, out var hash) && par.Value == hash),
            "Credenciais de outra conta foram alteradas.");
        await transacao.RollbackAsync();
    }

    [Fact]
    public async Task Segunda_execucao_preserva_hash_mesmo_com_segredo_diferente()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await using var transacao = await db.Database.BeginTransactionAsync();
        await LimparHashAsync(db);
        await Criar(db, SenhaTeste).GarantirAsync();
        var hashInicial = (await SupervisorAsync(db)).SenhaHash;

        await Criar(db, "Outro-segredo-ficticio!", new HasherProibido()).GarantirAsync();

        Assert.True(hashInicial is not null && hashInicial == (await SupervisorAsync(db)).SenhaHash,
            "Hash existente foi alterado.");
        await transacao.RollbackAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sem_segredo_preserva_estado_com_e_sem_hash(string? segredo)
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await using var transacao = await db.Database.BeginTransactionAsync();
        await LimparHashAsync(db);

        await Criar(db, segredo, new HasherProibido()).GarantirAsync();
        Assert.True((await SupervisorAsync(db)).SenhaHash is null, "Hash ausente foi alterado.");

        await Criar(db, SenhaTeste).GarantirAsync();
        var hashInicial = (await SupervisorAsync(db)).SenhaHash;
        await Criar(db, segredo, new HasherProibido()).GarantirAsync();
        Assert.True(hashInicial is not null && hashInicial == (await SupervisorAsync(db)).SenhaHash,
            "Hash existente foi alterado sem segredo.");
        await transacao.RollbackAsync();
    }

    [Fact]
    public async Task Atualizacao_condicional_preserva_hash_definido_entre_leitura_e_gravacao()
    {
        await using var db = await PostgresTestDatabase.CriarContextoAsync();
        await using var transacao = await db.Database.BeginTransactionAsync();
        await LimparHashAsync(db);
        var hasher = PasswordHasherDoCorretor.Criar();
        var vencedor = hasher.HashPassword(await SupervisorAsync(db), "Senha-concorrente-ficticia!");
        var intercalado = new HasherIntercalado(hasher, () =>
            db.Corretores.Where(c => c.Id == SupervisorId)
                .ExecuteUpdate(campos => campos.SetProperty(c => c.SenhaHash, vencedor)));

        await Criar(db, SenhaTeste, intercalado).GarantirAsync();

        Assert.True(intercalado.Executado, "Janela entre leitura e gravacao nao foi exercitada.");
        Assert.True(vencedor == (await SupervisorAsync(db)).SenhaHash,
            "Atualizacao condicional sobrescreveu hash definido por outra gravacao.");
        await transacao.RollbackAsync();
    }

    private static SenhaInicialSupervisor Criar(
        SolarDbContext db, string? segredo, IPasswordHasher<Corretor>? hasher = null) =>
        new(db, hasher ?? PasswordHasherDoCorretor.Criar(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SenhaInicialSupervisor.ChaveConfiguracao] = segredo,
            }).Build());

    private static Task<Corretor> SupervisorAsync(SolarDbContext db) =>
        db.Corretores.AsNoTracking().SingleAsync(c => c.Id == SupervisorId);

    private static async Task LimparHashAsync(SolarDbContext db)
    {
        var alterados = await db.Corretores.Where(c => c.Id == SupervisorId)
            .ExecuteUpdateAsync(campos => campos.SetProperty(c => c.SenhaHash, (string?)null));
        Assert.Equal(1, alterados);
    }

    private sealed class HasherProibido : IPasswordHasher<Corretor>
    {
        public string HashPassword(Corretor user, string password) =>
            throw new InvalidOperationException("Hasher nao deveria ser chamado.");

        public PasswordVerificationResult VerifyHashedPassword(Corretor user, string hashedPassword, string providedPassword) =>
            throw new NotSupportedException();
    }

    private sealed class HasherIntercalado(IPasswordHasher<Corretor> hasher, Action gravar) : IPasswordHasher<Corretor>
    {
        public bool Executado { get; private set; }

        public string HashPassword(Corretor user, string password)
        {
            gravar();
            Executado = true;
            return hasher.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(Corretor user, string hashedPassword, string providedPassword) =>
            hasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
