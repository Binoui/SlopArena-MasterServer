using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MasterServer.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueIndexGameServerIpPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_GameServers_IpAddress_Port",
                table: "GameServers",
                columns: new[] { "IpAddress", "Port" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GameServers_IpAddress_Port",
                table: "GameServers");
        }
    }
}
