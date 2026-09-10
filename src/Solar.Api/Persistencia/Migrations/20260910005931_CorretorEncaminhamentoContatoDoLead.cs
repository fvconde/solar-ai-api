using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class CorretorEncaminhamentoContatoDoLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "novo");

            migrationBuilder.AddColumn<string>(
                name: "telefone",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "corretores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    nome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    especialidade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    regioes = table.Column<List<string>>(type: "text[]", nullable: false),
                    contato_interno = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ativo = table.Column<bool>(type: "boolean", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_corretores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "encaminhamentos",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversa_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    corretor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    especialidade = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_encaminhamentos", x => x.id);
                    table.ForeignKey(
                        name: "fk_encaminhamentos_conversas_conversa_id",
                        column: x => x.conversa_id,
                        principalTable: "conversas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_encaminhamentos_corretores_corretor_id",
                        column: x => x.corretor_id,
                        principalTable: "corretores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_encaminhamentos_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });


            // A base de corretores, semeada aqui como os 80 imoveis sao semeados
            // por JSON. Literal dentro da migration de proposito: seed que le
            // codigo da aplicacao muda de resultado quando o codigo muda.
            // As duas especialidades cobrem as cinco zonas da base -- senao um
            // lead de investimento na zona leste cairia em "sem corretor".
            migrationBuilder.InsertData(
                table: "corretores",
                columns: new[] { "id", "nome", "especialidade", "regioes", "contato_interno", "ativo", "criado_em" },
                values: new object[,]
                {
                    { new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000001"), "Helena Braga", "moradia", new[] { "sul", "centro" }, "helena.braga@solar.local", true, new DateTimeOffset(2026, 9, 9, 0, 1, 0, TimeSpan.Zero) },
                    { new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000002"), "Rafael Nunes", "moradia", new[] { "oeste", "norte" }, "rafael.nunes@solar.local", true, new DateTimeOffset(2026, 9, 9, 0, 2, 0, TimeSpan.Zero) },
                    { new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000003"), "Beatriz Lima", "moradia", new[] { "leste", "centro", "norte" }, "beatriz.lima@solar.local", true, new DateTimeOffset(2026, 9, 9, 0, 3, 0, TimeSpan.Zero) },
                    { new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000004"), "Caio Ferraz", "investimento", new[] { "sul", "oeste", "centro" }, "caio.ferraz@solar.local", true, new DateTimeOffset(2026, 9, 9, 0, 4, 0, TimeSpan.Zero) },
                    { new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000005"), "Marina Alves", "investimento", new[] { "norte", "leste" }, "marina.alves@solar.local", true, new DateTimeOffset(2026, 9, 9, 0, 5, 0, TimeSpan.Zero) }
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_email",
                table: "leads",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_telefone",
                table: "leads",
                column: "telefone",
                unique: true,
                filter: "telefone IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_corretores_especialidade_ativo",
                table: "corretores",
                columns: new[] { "especialidade", "ativo" });

            migrationBuilder.CreateIndex(
                name: "ix_encaminhamentos_conversa_id",
                table: "encaminhamentos",
                column: "conversa_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_encaminhamentos_corretor_id",
                table: "encaminhamentos",
                column: "corretor_id");

            migrationBuilder.CreateIndex(
                name: "ix_encaminhamentos_lead_id",
                table: "encaminhamentos",
                column: "lead_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "corretores",
                keyColumn: "id",
                keyValues: new object[]
                {
                    new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000001"),
                    new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000002"),
                    new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000003"),
                    new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000004"),
                    new Guid("3f6b9c21-4d0a-4c7e-9a11-000000000005")
                });

            migrationBuilder.DropTable(
                name: "encaminhamentos");

            migrationBuilder.DropTable(
                name: "corretores");

            migrationBuilder.DropIndex(
                name: "ix_leads_email",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_telefone",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "email",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "status",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "telefone",
                table: "leads");
        }
    }
}
