using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleOrderReceivedAndRoundOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReceivedAmount",
                table: "SaleOrders",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RoundOffAmount",
                table: "SaleOrders",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // 历史数据回填「实收金额」：按零售行业口径补算——
            // 非赊账（现金/微信/支付宝）实收即应收净额；赊账为挂账、实际未收到钱，保持 0。
            // 新列默认值全为 0，此处不会覆盖任何已有语义的数据。
            migrationBuilder.Sql("UPDATE SaleOrders SET ReceivedAmount = PayAmount WHERE IsCredit = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceivedAmount",
                table: "SaleOrders");

            migrationBuilder.DropColumn(
                name: "RoundOffAmount",
                table: "SaleOrders");
        }
    }
}
