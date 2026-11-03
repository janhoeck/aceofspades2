using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AoS2.Backend.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerBrowser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "listed_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(270)", maxLength: 270, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    map = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    players = table.Column<int>(type: "integer", nullable: false),
                    max_players = table.Column<int>(type: "integer", nullable: false),
                    protocol_version = table.Column<int>(type: "integer", nullable: false),
                    game_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    official = table.Column<bool>(type: "boolean", nullable: false),
                    registered_from_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    registered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_listed_servers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_listed_servers_address",
                table: "listed_servers",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_listed_servers_last_heartbeat_at",
                table: "listed_servers",
                column: "last_heartbeat_at");

            migrationBuilder.CreateIndex(
                name: "ix_listed_servers_registered_from_ip",
                table: "listed_servers",
                column: "registered_from_ip");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listed_servers");
        }
    }
}
