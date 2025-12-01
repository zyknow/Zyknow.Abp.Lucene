using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Simple.Migrations
{
    /// <inheritdoc />
    public partial class Update_Abp_10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FrontChannelLogoutUri",
                table: "OpenIddictApplications",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrontChannelLogoutUri",
                table: "OpenIddictApplications");
        }
    }
}
