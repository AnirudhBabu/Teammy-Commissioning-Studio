using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teammy.Infrastructure.Migrations.Sqlite;

/// <inheritdoc />
public partial class InitialWorkspace : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_entries",
            columns: table => new
            {
                Revision = table.Column<int>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_entries", x => x.Revision);
            });

        migrationBuilder.CreateTable(
            name: "workspaces",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Revision = table.Column<int>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workspaces", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_entries");

        migrationBuilder.DropTable(
            name: "workspaces");
    }
}
