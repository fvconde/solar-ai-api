using Microsoft.EntityFrameworkCore;
using Solar.Api.Contracts;
using Solar.Api.Dominio;

namespace Solar.Api.Persistencia;

public sealed class SolarDbContext(DbContextOptions<SolarDbContext> options) : DbContext(options)
{
    public DbSet<Lead> Leads => Set<Lead>();

    public DbSet<Conversa> Conversas => Set<Conversa>();

    public DbSet<Mensagem> Mensagens => Set<Mensagem>();

    protected override void OnModelCreating(ModelBuilder modelo)
    {
        modelo.Entity<Lead>(lead =>
        {
            lead.HasKey(l => l.Id);
            lead.Property(l => l.Id).ValueGeneratedNever();
            lead.Property(l => l.Nome).HasMaxLength(200);
            lead.Property(l => l.Intencao).HasMaxLength(20);
            lead.Property(l => l.Regiao).HasMaxLength(120);
            lead.Property(l => l.Urgencia).HasMaxLength(20);
            lead.Property(l => l.ExpectativaRetorno).HasMaxLength(ContratoTurno.LimiteExpectativa);
        });

        modelo.Entity<Conversa>(conversa =>
        {
            conversa.HasKey(c => c.Id);

            // O id vem do cliente -- a conversa nasce no primeiro POST com o Guid
            // que ele escolheu. Sem isso o EF geraria um id proprio e ignoraria o
            // valor recebido.
            conversa.Property(c => c.Id).ValueGeneratedNever();
            conversa.Property(c => c.Canal).HasMaxLength(20).IsRequired();

            conversa.HasOne(c => c.Lead)
                .WithMany()
                .HasForeignKey(c => c.LeadId)
                .OnDelete(DeleteBehavior.Cascade);

            conversa.Navigation(c => c.Mensagens).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelo.Entity<Mensagem>(mensagem =>
        {
            mensagem.HasKey(m => m.Id);
            mensagem.Property(m => m.Papel).HasMaxLength(10).IsRequired();
            mensagem.Property(m => m.Texto).HasMaxLength(ContratoTurno.LimiteMensagem).IsRequired();
            mensagem.Property(m => m.ProximaAcao).HasMaxLength(30);

            mensagem.HasOne<Conversa>()
                .WithMany(c => c.Mensagens)
                .HasForeignKey(m => m.ConversaId)
                .OnDelete(DeleteBehavior.Cascade);

            // As duas consultas quentes -- ultimas N do POST e historico inteiro
            // do GET -- filtram por conversa e ordenam por id. O indice composto
            // atende as duas de uma vez.
            mensagem.HasIndex(m => new { m.ConversaId, m.Id });
        });

        AplicarSnakeCase(modelo);
    }

    /// <summary>
    /// O EF nomearia tabelas e colunas em PascalCase, e o Postgres exige aspas
    /// para ler identificador com maiuscula -- <c>select * from "Conversas"</c>.
    /// Renomear tudo para snake_case aqui deixa o banco legivel no psql e no
    /// DBeaver sem depender de pacote de convencao.
    /// </summary>
    private static void AplicarSnakeCase(ModelBuilder modelo)
    {
        foreach (var entidade in modelo.Model.GetEntityTypes())
        {
            if (entidade.GetTableName() is { } tabela)
            {
                entidade.SetTableName(ParaSnakeCase(tabela));
            }

            foreach (var propriedade in entidade.GetProperties())
            {
                propriedade.SetColumnName(ParaSnakeCase(propriedade.Name));
            }

            foreach (var chave in entidade.GetKeys())
            {
                chave.SetName(ParaSnakeCase(chave.GetName()!));
            }

            foreach (var estrangeira in entidade.GetForeignKeys())
            {
                estrangeira.SetConstraintName(ParaSnakeCase(estrangeira.GetConstraintName()!));
            }

            foreach (var indice in entidade.GetIndexes())
            {
                indice.SetDatabaseName(ParaSnakeCase(indice.GetDatabaseName()!));
            }
        }
    }

    /// <summary>
    /// <c>PrecoMin</c> vira <c>preco_min</c>; <c>PK_Leads</c> vira <c>pk_leads</c>,
    /// e nao <c>p_k_leads</c> -- o corte so acontece quando a maiuscula comeca
    /// uma palavra nova, nunca no meio de uma sigla.
    /// </summary>
    private static string ParaSnakeCase(string nome)
    {
        var construtor = new System.Text.StringBuilder(nome.Length + 8);

        for (var i = 0; i < nome.Length; i++)
        {
            var caractere = nome[i];

            if (char.IsUpper(caractere) && i > 0 && nome[i - 1] != '_' && ComecaPalavra(nome, i))
            {
                construtor.Append('_');
            }

            construtor.Append(char.ToLowerInvariant(caractere));
        }

        return construtor.ToString();
    }

    private static bool ComecaPalavra(string nome, int i) =>
        !char.IsUpper(nome[i - 1]) || (i + 1 < nome.Length && char.IsLower(nome[i + 1]));
}
