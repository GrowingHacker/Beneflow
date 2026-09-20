using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductPromo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OriginalPrice",
                table: "SaleOrderDetails",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "PromoEnabled",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromoEndAt",
                table: "Products",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromoPrice",
                table: "Products",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PromoRate",
                table: "Products",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromoStartAt",
                table: "Products",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromoType",
                table: "Products",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            // 既有明细都在「无档案优惠」时期成交，挂牌价 = 成交价，回填后历史单据的「原价」不会显示成 0
            migrationBuilder.Sql("UPDATE [SaleOrderDetails] SET [OriginalPrice] = [UnitPrice];");
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalPrice",
                table: "SaleOrderDetails");

            migrationBuilder.DropColumn(
                name: "PromoEnabled",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PromoEndAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PromoPrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PromoRate",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PromoStartAt",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PromoType",
                table: "Products");
        }
    }
}
