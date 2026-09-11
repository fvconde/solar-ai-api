using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solar.Api.Persistencia.Migrations
{
    /// <inheritdoc />
    public partial class ConsentimentoDoLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consentimento_em",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "versao_aviso_privacidade",
                table: "leads",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consentimento_em",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "versao_aviso_privacidade",
                table: "leads");
        }
    }
}
