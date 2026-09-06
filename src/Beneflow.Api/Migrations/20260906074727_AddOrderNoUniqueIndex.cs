using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderNoUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StockChecks_OrderNo",
                table: "StockChecks",
                column: "OrderNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_OrderNo",
                table: "SaleReturns",
                column: "OrderNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleOrders_OrderNo",
                table: "SaleOrders",
                column: "OrderNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_OrderNo",
                table: "PurchaseReturns",
                column: "OrderNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_OrderNo",
                table: "PurchaseOrders",
                column: "OrderNo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockChecks_OrderNo",
                table: "StockChecks");

            migrationBuilder.DropIndex(
                name: "IX_SaleReturns_OrderNo",
                table: "SaleReturns");

            migrationBuilder.DropIndex(
                name: "IX_SaleOrders_OrderNo",
                table: "SaleOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturns_OrderNo",
                table: "PurchaseReturns");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_OrderNo",
                table: "PurchaseOrders");
        }
    }
}
