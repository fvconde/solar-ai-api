using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class S44DedupePorConta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_email",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_telefone",
                table: "leads");

            migrationBuilder.CreateIndex(
                name: "ix_leads_email",
                table: "leads",
                column: "email",
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_telefone",
                table: "leads",
                column: "telefone",
                filter: "telefone IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_email",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_telefone",
                table: "leads");

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
        }
    }
}
