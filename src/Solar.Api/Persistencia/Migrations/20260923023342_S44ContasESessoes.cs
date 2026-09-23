using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations;

/// <inheritdoc />
public partial class S44ContasESessoes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "ix_corretores_especialidade_ativo", table: "corretores");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "expira_em", table: "sessoes", type: "timestamp with time zone",
            nullable: false, defaultValue: DateTimeOffset.MinValue);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "aprovado_em", table: "corretores", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "consentimento_em", table: "corretores", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<List<string>>(
            name: "especialidades", table: "corretores", type: "text[]", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "status_corretor", table: "corretores", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "telefone", table: "corretores", type: "character varying(11)", maxLength: 11, nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "versao_aviso_privacidade", table: "corretores", type: "character varying(40)", maxLength: 40, nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "conta_id", table: "conversas", type: "uuid", nullable: true);

        migrationBuilder.Sql("""
            UPDATE corretores
            SET especialidades = ARRAY[especialidade],
                status_corretor = CASE WHEN perfil = 'cliente' THEN NULL ELSE 'aprovado' END;
            UPDATE sessoes SET expira_em = criada_em + INTERVAL '30 days';
            ALTER TABLE corretores ALTER COLUMN especialidades SET NOT NULL;
            ALTER TABLE sessoes ALTER COLUMN expira_em DROP DEFAULT;
            """);

        migrationBuilder.DropColumn(name: "especialidade", table: "corretores");

        migrationBuilder.CreateIndex(
            name: "ix_corretores_especialidades", table: "corretores", column: "especialidades")
            .Annotation("Npgsql:IndexMethod", "gin");
        migrationBuilder.CreateIndex(
            name: "ix_corretores_status_corretor_criado_em", table: "corretores",
            columns: new[] { "status_corretor", "criado_em" });
        migrationBuilder.CreateIndex(name: "ix_conversas_conta_id", table: "conversas", column: "conta_id");
        migrationBuilder.AddForeignKey(
            name: "fk_conversas_corretores_conta_id", table: "conversas", column: "conta_id",
            principalTable: "corretores", principalColumn: "id", onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "fk_conversas_corretores_conta_id", table: "conversas");
        migrationBuilder.DropIndex(name: "ix_conversas_conta_id", table: "conversas");
        migrationBuilder.DropIndex(name: "ix_corretores_especialidades", table: "corretores");
        migrationBuilder.DropIndex(name: "ix_corretores_status_corretor_criado_em", table: "corretores");

        migrationBuilder.AddColumn<string>(
            name: "especialidade", table: "corretores", type: "character varying(20)",
            maxLength: 20, nullable: true);
        migrationBuilder.Sql("""
            UPDATE corretores
            SET especialidade = COALESCE(especialidades[1], 'moradia');
            ALTER TABLE corretores ALTER COLUMN especialidade SET NOT NULL;
            """);

        migrationBuilder.DropColumn(name: "conta_id", table: "conversas");
        migrationBuilder.DropColumn(name: "aprovado_em", table: "corretores");
        migrationBuilder.DropColumn(name: "consentimento_em", table: "corretores");
        migrationBuilder.DropColumn(name: "especialidades", table: "corretores");
        migrationBuilder.DropColumn(name: "status_corretor", table: "corretores");
        migrationBuilder.DropColumn(name: "telefone", table: "corretores");
        migrationBuilder.DropColumn(name: "versao_aviso_privacidade", table: "corretores");
        migrationBuilder.DropColumn(name: "expira_em", table: "sessoes");

        migrationBuilder.CreateIndex(
            name: "ix_corretores_especialidade_ativo", table: "corretores",
            columns: new[] { "especialidade", "ativo" });
    }
}
