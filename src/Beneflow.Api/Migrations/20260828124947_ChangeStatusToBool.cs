using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beneflow.Api.Migrations
{
    /// <inheritdoc />
    public partial class ChangeStatusToBool : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 先把原字符串状态统一清洗成 "1" / "0"，否则 ALTER COLUMN 到 bit 会因"停用/下架"无法转换而报错。
            // UserInfo / Roles / Suppliers 用 启用=1，其余一律 0；Products 上架=1；Categories 启用=1。
            migrationBuilder.Sql(@"UPDATE [UserInfo]  SET [Status] = CASE WHEN [Status] IN (N'启用',N'1',N'TRUE',N'true') THEN N'1' ELSE N'0' END;
                                   UPDATE [Roles]     SET [Status] = CASE WHEN [Status] IN (N'启用',N'1',N'TRUE',N'true') THEN N'1' ELSE N'0' END;
                                   UPDATE [Suppliers] SET [Status] = CASE WHEN [Status] IN (N'启用',N'1',N'TRUE',N'true') THEN N'1' ELSE N'0' END;
                                   UPDATE [Products]  SET [Status] = CASE WHEN [Status] IN (N'上架',N'启用',N'1',N'TRUE',N'true') THEN N'1' ELSE N'0' END;
                                   UPDATE [Categories]SET [Status] = CASE WHEN [Status] IN (N'启用',N'1',N'TRUE',N'true') THEN N'1' ELSE N'0' END;");

            migrationBuilder.AlterColumn<bool>(
                name: "Status",
                table: "UserInfo",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldDefaultValueSql: "N'启用'");

            migrationBuilder.AlterColumn<bool>(
                name: "Status",
                table: "Suppliers",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldDefaultValueSql: "N'启用'");

            migrationBuilder.AlterColumn<bool>(
                name: "Status",
                table: "Roles",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<bool>(
                name: "Status",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldDefaultValueSql: "N'上架'");

            migrationBuilder.AlterColumn<bool>(
                name: "Status",
                table: "Categories",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 顺序：先把 bit 列放宽成 nvarchar（此时 1→"True" / 0→"False"），再 UPDATE 为中文语义值。
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "UserInfo",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValueSql: "N'启用'",
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);
            migrationBuilder.Sql(@"UPDATE [UserInfo] SET [Status] = CASE WHEN LOWER([Status]) IN (N'true',N'1') THEN N'启用' ELSE N'禁用' END WHERE [Status] IN (N'True',N'False',N'true',N'false',N'1',N'0');");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Suppliers",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValueSql: "N'启用'",
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);
            migrationBuilder.Sql(@"UPDATE [Suppliers] SET [Status] = CASE WHEN LOWER([Status]) IN (N'true',N'1') THEN N'启用' ELSE N'停用' END WHERE [Status] IN (N'True',N'False',N'true',N'false',N'1',N'0');");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Roles",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);
            migrationBuilder.Sql(@"UPDATE [Roles] SET [Status] = CASE WHEN LOWER([Status]) IN (N'true',N'1') THEN N'启用' ELSE N'禁用' END WHERE [Status] IN (N'True',N'False',N'true',N'false',N'1',N'0');");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Products",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValueSql: "N'上架'",
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);
            migrationBuilder.Sql(@"UPDATE [Products] SET [Status] = CASE WHEN LOWER([Status]) IN (N'true',N'1') THEN N'上架' ELSE N'下架' END WHERE [Status] IN (N'True',N'False',N'true',N'false',N'1',N'0');");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Categories",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);
            migrationBuilder.Sql(@"UPDATE [Categories] SET [Status] = CASE WHEN LOWER([Status]) IN (N'true',N'1') THEN N'启用' ELSE N'禁用' END WHERE [Status] IN (N'True',N'False',N'true',N'false',N'1',N'0');");
        }
    }
}
