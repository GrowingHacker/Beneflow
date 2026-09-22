using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseReturnVoided : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsVoided",
                table: "PurchaseReturns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "VoidedAt",
                table: "PurchaseReturns",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsVoided",
                table: "PurchaseReturns");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                table: "PurchaseReturns");
        }
    }
}
