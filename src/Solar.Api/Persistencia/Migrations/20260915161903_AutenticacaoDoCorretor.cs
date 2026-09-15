using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class AutenticacaoDoCorretor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bloqueado_ate",
                table: "corretores",
                type: "timestamp with time zone",
                nullable: true);

migrationBuilder.AddColumn<string>(
 name: "email",
 table: "corretores",
 type: "character varying(320)",
 maxLength: 320,
 nullable: true);

migrationBuilder.AddColumn<string>(
 name: "email_normalizado",
 table: "corretores",
 type: "character varying(320)",
 maxLength: 320,
 nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "senha_hash",
                table: "corretores",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

migrationBuilder.AddColumn<int>(
 name: "tentativas_senha",
 table: "corretores",
 type: "integer",
 nullable: false,
 defaultValue: 0);

 // As linhas semeadas da migration de corretores ja possuem um e-mail
 // operacional em contato_interno. Copiar o valor evita senha ou credencial
 // padrao inventada durante a migracao; a senha segue nula ate provisionamento.
 migrationBuilder.Sql("""
 UPDATE corretores
 SET email = COALESCE(NULLIF(BTRIM(contato_interno), ''), id::text || '@solar.local'),
     email_normalizado = LOWER(COALESCE(NULLIF(BTRIM(contato_interno), ''), id::text || '@solar.local'))
 WHERE email IS NULL OR email_normalizado IS NULL;
 """);

 migrationBuilder.AlterColumn<string>(
  name: "email",
  table: "corretores",
  type: "character varying(320)",
  maxLength: 320,
  nullable: false,
  oldClrType: typeof(string),
  oldType: "character varying(320)",
  oldMaxLength: 320,
  oldNullable: true);

 migrationBuilder.AlterColumn<string>(
  name: "email_normalizado",
  table: "corretores",
  type: "character varying(320)",
  maxLength: 320,
  nullable: false,
  oldClrType: typeof(string),
  oldType: "character varying(320)",
  oldMaxLength: 320,
  oldNullable: true);

            migrationBuilder.CreateTable(
                name: "recuperacoes_senha",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    corretor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expira_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invalidada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recuperacoes_senha", x => x.id);
                    table.ForeignKey(
                        name: "fk_recuperacoes_senha_corretores_corretor_id",
                        column: x => x.corretor_id,
                        principalTable: "corretores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessoes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    corretor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    criada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revogada_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessoes", x => x.id);
                    table.ForeignKey(
                        name: "fk_sessoes_corretores_corretor_id",
                        column: x => x.corretor_id,
                        principalTable: "corretores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

        migrationBuilder.Sql("""
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM corretores
                GROUP BY email_normalizado
                HAVING COUNT(*) > 1
            ) THEN
                RAISE EXCEPTION 'AutenticacaoDoCorretor: email_normalizado duplicado; resolva os registros antes de criar o indice unico';
            END IF;
        END $$;
        """);

        migrationBuilder.CreateIndex(
            name: "ix_corretores_email_normalizado",
                table: "corretores",
                column: "email_normalizado",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recuperacoes_senha_corretor_id",
                table: "recuperacoes_senha",
                column: "corretor_id");

            migrationBuilder.CreateIndex(
                name: "ix_recuperacoes_senha_token_hash",
                table: "recuperacoes_senha",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessoes_corretor_id",
                table: "sessoes",
                column: "corretor_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessoes_token_hash",
                table: "sessoes",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recuperacoes_senha");

            migrationBuilder.DropTable(
                name: "sessoes");

            migrationBuilder.DropIndex(
                name: "ix_corretores_email_normalizado",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "bloqueado_ate",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "email",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "email_normalizado",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "senha_hash",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "tentativas_senha",
                table: "corretores");
        }
    }
}
