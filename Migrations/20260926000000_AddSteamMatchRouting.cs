using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260926000000_AddSteamMatchRouting")]
public sealed class AddSteamMatchRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("SteamId", "GameServers", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<int>("ProtocolVersion", "GameServers", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<Guid>("InstanceId", "GameServers", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("CatalogHash", "GameServers", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<Guid>("ServerId", "Matches", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>("CanceledAt", "Matches", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>("CancelReason", "Matches", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.CreateIndex("IX_Matches_ServerId", "Matches", "ServerId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Matches_ServerId", "Matches");
        migrationBuilder.DropColumn("CancelReason", "Matches");
        migrationBuilder.DropColumn("CanceledAt", "Matches");
        migrationBuilder.DropColumn("ServerId", "Matches");
        migrationBuilder.DropColumn("CatalogHash", "GameServers");
        migrationBuilder.DropColumn("InstanceId", "GameServers");
        migrationBuilder.DropColumn("ProtocolVersion", "GameServers");
        migrationBuilder.DropColumn("SteamId", "GameServers");
    }
}
