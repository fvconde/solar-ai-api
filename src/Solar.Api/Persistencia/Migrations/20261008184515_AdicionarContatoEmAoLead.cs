using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class AdicionarContatoEmAoLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "contato_em",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contato_em",
                table: "leads");
        }
    }
}
