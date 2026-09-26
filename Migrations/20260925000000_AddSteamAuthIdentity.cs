using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260925000000_AddSteamAuthIdentity")]
public sealed class AddSteamAuthIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AuthProvider",
            table: "Users",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "guest");

        migrationBuilder.CreateTable(
            name: "UsedSteamAuthTickets",
            columns: table => new
            {
                Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_UsedSteamAuthTickets", row => row.Hash));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UsedSteamAuthTickets");
        migrationBuilder.DropColumn(name: "AuthProvider", table: "Users");
    }
}
