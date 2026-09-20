using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleOrderDiscountRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DiscountRate",
                table: "SaleOrders",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscountRate",
                table: "SaleOrders");
        }
    }
}
