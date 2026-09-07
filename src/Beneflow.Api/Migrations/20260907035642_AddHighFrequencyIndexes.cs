using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHighFrequencyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StockCheckDetails_CheckId",
                table: "StockCheckDetails",
                column: "CheckId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleOrders_CreatedAt",
                table: "SaleOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnDetails_ReturnId_ProductId",
                table: "PurchaseReturnDetails",
                columns: new[] { "ReturnId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_CreatedAt",
                table: "PurchaseOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderDetails_OrderId",
                table: "PurchaseOrderDetails",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLogs_CreatedAt",
                table: "OperationLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CreditSales_CreatedAt",
                table: "CreditSales",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_ExpireDate",
                table: "Batches",
                column: "ExpireDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockCheckDetails_CheckId",
                table: "StockCheckDetails");

            migrationBuilder.DropIndex(
                name: "IX_SaleOrders_CreatedAt",
                table: "SaleOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseReturnDetails_ReturnId_ProductId",
                table: "PurchaseReturnDetails");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrders_CreatedAt",
                table: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderDetails_OrderId",
                table: "PurchaseOrderDetails");

            migrationBuilder.DropIndex(
                name: "IX_OperationLogs_CreatedAt",
                table: "OperationLogs");

            migrationBuilder.DropIndex(
                name: "IX_CreditSales_CreatedAt",
                table: "CreditSales");

            migrationBuilder.DropIndex(
                name: "IX_Batches_ExpireDate",
                table: "Batches");
        }
    }
}
