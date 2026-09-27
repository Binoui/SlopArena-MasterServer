using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260927000000_AddRoomMatchIdentity")]
public sealed class AddRoomMatchIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("RoomId", "Matches", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex("IX_Matches_RoomId", "Matches", "RoomId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Matches_RoomId", "Matches");
        migrationBuilder.DropColumn("RoomId", "Matches");
    }
}
