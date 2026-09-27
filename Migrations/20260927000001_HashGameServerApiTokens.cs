using MasterServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260927000001_HashGameServerApiTokens")]
public sealed class HashGameServerApiTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Preserve authority for already-running hosts across the schema cutover.
        migrationBuilder.Sql("UPDATE \"GameServers\" SET \"ApiToken\" = encode(sha256(convert_to(\"ApiToken\", 'UTF8')), 'hex');");
        migrationBuilder.RenameColumn("ApiToken", "GameServers", "ApiTokenHash");
        migrationBuilder.AlterColumn<string>(
            name: "ApiTokenHash", table: "GameServers", type: "character varying(64)",
            maxLength: 64, nullable: false, oldClrType: typeof(string),
            oldType: "character varying(128)", oldMaxLength: 128);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("GameServer API token hashes cannot be converted back to bearer tokens.");
}
