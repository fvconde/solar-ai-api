using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class PerfilEVinculoDoCorretor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "perfil",
                table: "corretores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "corretor");

        migrationBuilder.AddColumn<bool>(
            name: "vinculo_ativo",
            table: "corretores",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        // Supervisores de demonstração não recebem senha padrão. O provisionamento
        // de credenciais continua sendo responsabilidade do fluxo de acesso.
        migrationBuilder.Sql("""
            INSERT INTO corretores
                (id, nome, especialidade, regioes, contato_interno, ativo, criado_em,
                 bloqueado_ate, email, email_normalizado, senha_hash, tentativas_senha,
                 perfil, vinculo_ativo)
            VALUES
                ('3f6b9c21-4d0a-4c7e-9a11-000000000101', 'Supervisor Vinculado', 'moradia',
                 ARRAY['sul', 'centro']::text[], 'supervisor.vinculado@solar.local', TRUE,
                 TIMESTAMPTZ '2026-09-15 00:10:00+00', NULL,
                 'supervisor.vinculado@solar.local', 'supervisor.vinculado@solar.local', NULL, 0,
                 'supervisor', TRUE),
                ('3f6b9c21-4d0a-4c7e-9a11-000000000102', 'Supervisor Sem Vinculo', 'moradia',
                 ARRAY['norte', 'leste']::text[], 'supervisor.sem.vinculo@solar.local', TRUE,
                 TIMESTAMPTZ '2026-09-15 00:11:00+00', NULL,
                 'supervisor.sem.vinculo@solar.local', 'supervisor.sem.vinculo@solar.local', NULL, 0,
                 'supervisor', FALSE)
            ON CONFLICT (id) DO NOTHING;
            """);
        }

        /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM corretores
            WHERE id IN (
                '3f6b9c21-4d0a-4c7e-9a11-000000000101',
                '3f6b9c21-4d0a-4c7e-9a11-000000000102');
            """);

        migrationBuilder.DropColumn(
                name: "perfil",
                table: "corretores");

            migrationBuilder.DropColumn(
                name: "vinculo_ativo",
                table: "corretores");
        }
    }
}
