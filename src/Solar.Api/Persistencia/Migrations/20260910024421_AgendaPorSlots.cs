using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class AgendaPorSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "slot_id",
                table: "mensagens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_agendamento",
                table: "mensagens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "slots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    corretor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inicio = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fim = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slots", x => x.id);
                    table.ForeignKey(
                        name: "fk_slots_corretores_corretor_id",
                        column: x => x.corretor_id,
                        principalTable: "corretores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_slots_leads_lead_id",
                        column: x => x.lead_id,
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mensagens_slot_id",
                table: "mensagens",
                column: "slot_id");

            migrationBuilder.CreateIndex(
                name: "ix_slots_corretor_id_inicio",
                table: "slots",
                columns: new[] { "corretor_id", "inicio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_slots_lead_id",
                table: "slots",
                column: "lead_id");

            migrationBuilder.AddForeignKey(
                name: "fk_mensagens_slots_slot_id",
                table: "mensagens",
                column: "slot_id",
                principalTable: "slots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_mensagens_slots_slot_id",
                table: "mensagens");

            migrationBuilder.DropTable(
                name: "slots");

            migrationBuilder.DropIndex(
                name: "ix_mensagens_slot_id",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "slot_id",
                table: "mensagens");

            migrationBuilder.DropColumn(
                name: "status_agendamento",
                table: "mensagens");
        }
    }
}
