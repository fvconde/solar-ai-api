using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class RegistrarEtapasDasConversas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "corretor_atribuido_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "encaminhada_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "essenciais_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "intencao_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "primeiro_reengajamento_em",
                table: "conversas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "registro_metricas",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    historico_desde = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registro_metricas", x => x.id);
                    table.CheckConstraint("ck_registro_metricas_id", "id = 1");
                });

            migrationBuilder.Sql("INSERT INTO registro_metricas (id, historico_desde) VALUES (1, now());");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registro_metricas");

            migrationBuilder.DropColumn(
                name: "corretor_atribuido_em",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "encaminhada_em",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "essenciais_em",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "intencao_em",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "primeiro_reengajamento_em",
                table: "conversas");
        }
    }
}
