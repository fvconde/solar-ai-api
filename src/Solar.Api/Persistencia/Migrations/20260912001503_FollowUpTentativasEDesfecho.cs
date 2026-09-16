using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class FollowUpTentativasEDesfecho : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "desfecho",
                table: "conversas",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tentativas_reengajamento",
                table: "conversas",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "desfecho",
                table: "conversas");

            migrationBuilder.DropColumn(
                name: "tentativas_reengajamento",
                table: "conversas");
        }
    }
}
