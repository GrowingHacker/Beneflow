using Beneflow.Api.Models.Entities;
using Beneflow.Api.Utils;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Beneflow.Api.Data;

/// <summary>
/// 首次启动种子数据：与前端演示数据对齐（admin/cashier/buyer，密码 123456）。
/// 库存流水通过「事件回放」（期初 → 进货 → 销售）生成，保证账实一致。
/// </summary>
public static class DbSeeder
{
    private static readonly (string Name, string Desc, string Code, string[] Perms)[] RoleDefs =
    {
        ("店主",   "全部权限",                     "owner",
            new[] { "*" }),
        ("收银员", "销售收银、挂单、退货、赊账",  "cashier",
            new[] { "dashboard", "sales", "sale-returns", "credits", "inventory" }),
        ("采购员", "商品、供应商、采购",           "buyer",
            new[] { "dashboard", "products", "suppliers", "purchases", "purchase-returns", "inventory", "stock-warnings", "expiry" }),
        ("仓库员", "库存管理、盘点",               "keeper",
            new[] { "dashboard", "inventory", "stock-warnings", "expiry", "stock-check", "stock-log" }),
        ("财务",   "报表查看、对账、导出",         "finance",
            new[] { "dashboard", "reports", "credits" }),
    };

    // 与前端 app.js MENU_CONFIG 顺序一致：(权限码, 菜单名)
    private static readonly (string Code, string Label)[] MenuDefs =
    {
        ("dashboard", "首页看板"), ("sales", "收银台"), ("sale-returns", "销售退货"), ("credits", "赊账管理"),
        ("products", "商品档案"), ("suppliers", "供应商管理"),
        ("purchases", "进货单"), ("purchase-returns", "采购退货"),
        ("inventory", "实时库存"), ("stock-warnings", "库存预警"), ("expiry", "临期商品"),
        ("stock-check", "盘点单"), ("stock-log", "库存流水"),
        ("reports", "财务报表"),
        ("users", "用户管理"), ("roles", "角色管理"), ("menus", "菜单管理"), ("logs", "操作日志"), ("settings", "系统设置"),
    };

    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync()) return;

        // ---------- 菜单 ----------
        var groupNames = new[] { "销售管理", "商品管理", "采购管理", "库存管理", "系统管理" };
        foreach (var g in groupNames)
        {
            db.Menus.Add(new Menu { Name = g, Type = "目录", Sort = 100 });
        }
        for (var i = 0; i < MenuDefs.Length; i++)
        {
            db.Menus.Add(new Menu { Name = MenuDefs[i].Label, Type = "菜单", PermCode = MenuDefs[i].Code, Sort = i });
        }
        await db.SaveChangesAsync();

        // 与前端 MENU_CONFIG 分组一致；dashboard、reports 为顶级菜单无目录
        static string GroupOf(int pageIdx) => pageIdx switch
        {
            1 or 2 or 3 => "销售管理",
            4 or 5 => "商品管理",
            6 or 7 => "采购管理",
            >= 8 and <= 12 => "库存管理",
            >= 14 => "系统管理",
            _ => "",
        };
        var allMenus = await db.Menus.ToListAsync();
        var dirByName = allMenus.Where(m => m.Type == "目录").ToDictionary(m => m.Name);

        // ---------- 角色 + 授权 + 用户 ----------
        var roleByCode = new Dictionary<string, Role>();
        foreach (var r in RoleDefs)
        {
            var role = new Role { Name = r.Name, Code = r.Code, Description = r.Desc };
            db.Roles.Add(role);
            roleByCode[r.Code] = role;
        }
        await db.SaveChangesAsync();

        var admin = NewUser("admin", "店主", "13800000001");
        var cashier = NewUser("cashier", "收银员", "13800000002");
        var buyer = NewUser("buyer", "采购员", "13800000003");
        db.Users.AddRange(admin, cashier, buyer);
        await db.SaveChangesAsync();

        db.UserRoles.AddRange(
            new UserRole { UserId = admin.Id, RoleId = roleByCode["owner"].Id },
            new UserRole { UserId = cashier.Id, RoleId = roleByCode["cashier"].Id },
            new UserRole { UserId = buyer.Id, RoleId = roleByCode["buyer"].Id });

        // 复合主键去重：同一角色下多个权限可能归属同一目录
        var addedRoleMenus = new HashSet<(int RoleId, int MenuId)>();
        void AddRoleMenu(int roleId, int menuId)
        {
            if (addedRoleMenus.Add((roleId, menuId)))
                db.RoleMenus.Add(new RoleMenu { RoleId = roleId, MenuId = menuId });
        }

        foreach (var r in RoleDefs)
        {
            var roleId = roleByCode[r.Code].Id;
            if (r.Perms.Contains("*"))
            {
                foreach (var m in allMenus)
                    AddRoleMenu(roleId, m.Id);
                continue;
            }
            foreach (var perm in r.Perms)
            {
                var m = allMenus.First(x => x.PermCode == perm);
                AddRoleMenu(roleId, m.Id);

                var dirName = GroupOf(m.Sort);
                if (!string.IsNullOrEmpty(dirName))
                    AddRoleMenu(roleId, dirByName[dirName].Id);
            }
        }

        // ---------- 分类 / 商品 / 批次 ----------
        var catNames = new[] { "烟酒", "饮料", "零食", "调味", "日用" };
        foreach (var n in catNames)
        {
            db.Categories.Add(new ProductCategory { Name = n, CreatedAt = DateTime.Parse("2026-08-01 08:00") });
        }
        await db.SaveChangesAsync();

        // 保存后才能拿到自增 Id
        var catIds = await db.Categories.ToDictionaryAsync(c => c.Name, c => c.Id);

        // 演示商品：与原 MockControllers 数据对齐
        var products = new List<Product>
        {
            new() { Barcode="6901028075138", Name="中华（软）香烟", CategoryId=catIds["烟酒"], Unit="包", Spec="20支", SalePrice=65.00m, CostPrice=58.00m, StockQuantity=48, SafetyStock=10 },
            new() { Barcode="6920202888823", Name="可口可乐 330ml", CategoryId=catIds["饮料"], Unit="罐", Spec="330ml", SalePrice=2.50m, CostPrice=1.80m, StockQuantity=240, SafetyStock=60, HasExpiry=true, ShelfLifeDays=270 },
            new() { Barcode="6924743912013", Name="康师傅红烧牛肉面", CategoryId=catIds["饮料"], Unit="包", Spec="100g", SalePrice=3.00m, CostPrice=2.10m, StockQuantity=150, SafetyStock=40, HasExpiry=true, ShelfLifeDays=180 },
            new() { Barcode="6901285991219", Name="农夫山泉 550ml", CategoryId=catIds["饮料"], Unit="瓶", Spec="550ml", SalePrice=2.00m, CostPrice=1.00m, StockQuantity=8, SafetyStock=50, HasExpiry=true, ShelfLifeDays=540 },
            new() { Barcode="6921168509256", Name="乐事黄瓜味薯片", CategoryId=catIds["零食"], Unit="包", Spec="70g", SalePrice=7.50m, CostPrice=5.50m, StockQuantity=70, SafetyStock=20, HasExpiry=true, ShelfLifeDays=25 },
            new() { Barcode="6901668003420", Name="海天金标生抽", CategoryId=catIds["调味"], Unit="瓶", Spec="500ml", SalePrice=12.80m, CostPrice=9.50m, StockQuantity=35, SafetyStock=8 },
            new() { Barcode="6925921202195", Name="舒肤佳香皂", CategoryId=catIds["日用"], Unit="块", Spec="115g", SalePrice=6.90m, CostPrice=4.80m, StockQuantity=60, SafetyStock=15 },
            new() { Barcode="6902261909063", Name="清风原木抽纸", CategoryId=catIds["日用"], Unit="包", Spec="200抽", SalePrice=5.90m, CostPrice=4.00m, StockQuantity=5, SafetyStock=20 },
        };
        foreach (var p in products)
        {
            p.CreatedAt = DateTime.Parse("2026-08-01 08:00");
            p.UpdatedAt = p.CreatedAt;
            db.Products.Add(p);
        }
        await db.SaveChangesAsync();

        // 有效期批次（沿用演示语义：农夫山泉已过期、薯片临期）
        var expireOffsets = new Dictionary<int, int> { [2] = 270, [3] = 180, [4] = -1, [5] = 25 }; // products 下标从 0 计
        foreach (var (idx0, daysLeft) in expireOffsets)
        {
            var p = products[idx0];
            var expire = DateTime.Today.AddDays(daysLeft);
            db.Batches.Add(new ProductBatch
            {
                ProductId = p.Id, BatchNo = $"B{expire:yyMMdd}{idx0:D2}",
                ProduceDate = expire.AddDays(-p.ShelfLifeDays),
                ExpireDate = expire, Quantity = p.StockQuantity,
                CreatedAt = DateTime.Parse("2026-08-05 10:00"),
            });
        }

        // ---------- 供应商 ----------
        var sup1 = new Supplier { Name = "华南批发商行", Contact = "张经理", Phone = "13800001111", Address = "广州市天河区", CreatedAt = DateTime.Parse("2026-08-01 09:00") };
        var sup2 = new Supplier { Name = "永辉供货", Contact = "李姐", Phone = "13900002222", Address = "深圳市南山区", CreatedAt = DateTime.Parse("2026-08-02 09:00") };
        var sup3 = new Supplier { Name = "本地烟酒行", Contact = "王哥", Phone = "13700003333", Address = "中山市东区", Status = false, CreatedAt = DateTime.Parse("2026-08-03 09:00") };
        db.Suppliers.AddRange(sup1, sup2, sup3);
        await db.SaveChangesAsync();

        // ---------- 业务事件回放：期初建账 → 进货入库 → 销售出库 ----------
        var qty = products.ToDictionary(p => p.Id, p => 0m);   // 回放中的实时库存

        void LogStock(Product p, decimal change, string type, string refNo, int by, DateTime at)
        {
            db.StockLogs.Add(new StockLog
            {
                ProductId = p.Id, ChangeType = type, ChangeQty = change,
                BeforeQty = qty[p.Id], AfterQty = qty[p.Id] + change,
                RefNo = refNo, CreatedBy = by, CreatedAt = at,
            });
            qty[p.Id] += change;
        }

        // 期初 = 当前库存 + 已售 − 已购（保证回放后恰好等于当前库存）
        var openings = new Dictionary<int, decimal> { [1] = 1, [2] = 69, [3] = 91, [4] = 12, [5] = 56, [6] = 5, [7] = 50, [8] = 3 }; // products 序号从 1 起
        foreach (var op in openings)
        {
            LogStock(products[op.Key - 1], op.Value, "期初建账", "INIT", admin.Id, DateTime.Parse("2026-08-01 08:00"));
        }

        // 进货单
        var buyerId = buyer.Id;
        var poDefs = new (DateTime At, Supplier Sup, (Product P, decimal Qty, decimal Cost)[] Lines)[]
        {
            (DateTime.Parse("2026-08-20 14:20"), sup1, new[]
            {
                (products[0], 48m, 10.20m), (products[4], 20m, 4.50m),
                (products[6], 10m, 2.50m), (products[7], 2m, 3.00m),
            }),
            (DateTime.Parse("2026-08-22 09:05"), sup2, new[]
            {
                (products[1], 200m, 1.40m), (products[2], 100m, 1.80m), (products[5], 30m, 8.00m),
            }),
        };
        for (var i = 0; i < poDefs.Length; i++)
        {
            var def = poDefs[i];
            var order = new PurchaseOrder
            {
                OrderNo = $"PO{def.At:yyyyMMdd}{i + 1:D3}", SupplierId = def.Sup.Id,
                CreatedBy = buyerId, CreatedAt = def.At,
                TotalQty = def.Lines.Sum(l => l.Qty),
                TotalAmount = Math.Round(def.Lines.Sum(l => l.Qty * l.Cost), 2),
            };
            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync();

            foreach (var line in def.Lines)
            {
                db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
                {
                    OrderId = order.Id, ProductId = line.P.Id,
                    Qty = line.Qty, CostPrice = line.Cost, SubTotal = Math.Round(line.Qty * line.Cost, 2),
                });
                LogStock(line.P, line.Qty, "采购入库", order.OrderNo, buyerId, def.At);
            }
        }

        // 销售单（含赊账两条：一条已结清、一条未结清）
        var sales = new (DateTime At, UserInfo By, string Pay, bool Credit, string? Wx, decimal Discount, decimal CashGot, (Product P, decimal Qty)[] Lines)[]
        {
            (DateTime.Parse("2026-08-18 11:20"), admin, "赊账", true, "wx_li666", 0m, 0m, new[]{ (products[1], 24m) }),              // 60.00 已结清
            (DateTime.Parse("2026-08-25 16:48"), admin, "赊账", true, "wx_abc123", 0m, 0m, new[]{ (products[2], 40m) }),             // 120.00 未结清
            (DateTime.Parse("2026-08-26 09:12"), cashier, "现金", false, null, 0m, 50m, new[]{ (products[4], 5m), (products[1], 2m), (products[2], 1m) }),   // 45.50 找零 4.50
            (DateTime.Parse("2026-08-26 10:35"), cashier, "微信", false, null, 3m, 0m, new[]{ (products[0], 1m), (products[3], 4m), (products[1], 3m), (products[4], 1m) }), // 88-3=85
        };
        for (var i = 0; i < sales.Length; i++)
        {
            var s = sales[i];
            var total = Math.Round(s.Lines.Sum(l => l.P.SalePrice * l.Qty), 2);
            var payAmount = total - s.Discount;
            var orderNo = $"SO{s.At:yyyyMMdd}{i + 1:D3}";
            var so = new SaleOrder
            {
                OrderNo = orderNo, TotalAmount = total, DiscountAmount = s.Discount,
                PayAmount = payAmount, PayMethod = s.Pay,
                CashAmount = s.Pay == "现金" ? s.CashGot : 0,
                ChangeAmount = s.Pay == "现金" ? s.CashGot - payAmount : 0,
                IsCredit = s.Credit, WechatId = s.Wx, CreatedBy = s.By.Id, CreatedAt = s.At,
            };
            db.SaleOrders.Add(so);
            await db.SaveChangesAsync();

            foreach (var line in s.Lines)
            {
                db.SaleOrderDetails.Add(new SaleOrderDetail
                {
                    OrderId = so.Id, ProductId = line.P.Id, ProductName = line.P.Name, Barcode = line.P.Barcode,
                    Quantity = line.Qty, UnitPrice = line.P.SalePrice, CostPrice = line.P.CostPrice,
                    SubTotal = Math.Round(line.Qty * line.P.SalePrice, 2),
                });
                LogStock(line.P, -line.Qty, "销售出库", orderNo, s.By.Id, s.At);
            }

            if (s.Credit && s.Wx != null)
            {
                var settled = i == 0; // 第一条赊账演示为已结清
                var cs = new CreditSale
                {
                    SaleOrderId = so.Id, WechatId = s.Wx, CreditAmount = payAmount,
                    Phone = settled ? "13800138000" : "13900139000",
                    Remark = settled ? "已按约定结清" : "待跟进还款",
                    PaidAmount = settled ? payAmount : 0,
                    RemainingAmount = settled ? 0 : payAmount,
                    Status = settled,
                    SettledAt = settled ? DateTime.Parse("2026-08-20 19:00") : null,
                    CreatedAt = s.At,
                };
                db.CreditSales.Add(cs);
                await db.SaveChangesAsync(); // 先保存才能拿到 cs.Id

                if (settled)
                {
                    db.CreditPayments.Add(new CreditPayment
                    {
                        CreditSaleId = cs.Id, PayAmount = payAmount, PayMethod = "微信",
                        CreatedBy = admin.Id, CreatedAt = DateTime.Parse("2026-08-20 19:00"),
                    });
                }
            }
        }

        // 校验：回放终值必须等于商品当前库存
        foreach (var p in products)
        {
            if (qty[p.Id] != p.StockQuantity)
                throw new InvalidOperationException($"种子回放不一致：{p.Name} 回放={qty[p.Id]} 目标={p.StockQuantity}");
        }

        // ---------- 系统参数 ----------
        void SetCfg(string key, object val) => db.SystemConfigs.Add(new SystemConfig
        {
            ConfigKey = key, ConfigValue = JsonSerializer.Serialize(val), UpdatedAt = DateTime.Now,
        });
        SetCfg("shop", new { name = "百惠通便利店", phone = "020-88888888", address = "中山市东区XX路1号", logo = "" });
        SetCfg("sale", new { allowCredit = true, defaultPayMethod = "现金" });
        SetCfg("receipt", new { header = "百惠通便利店", footer = "欢迎再次光临", autoPrint = false });
        SetCfg("stock", new { warningThreshold = 1, expiryDays = 30 });
        SetCfg("units", new[] { "个", "瓶", "盒", "箱", "袋", "包", "罐", "听", "支", "条", "桶", "杯", "份", "块", "双", "张", "斤", "千克", "克", "升", "毫升" });
        SetCfg("specs", new[] { "常规", "散装", "500ml", "330ml", "550ml", "250ml", "1L", "1.5L", "500g", "250g", "100g", "1kg", "10kg", "20支" });
        // 分类专属单位/规格：与上方演示商品的分类、单位、规格保持一致
        SetCfg("categoryDict", new Dictionary<string, object>
        {
            ["烟酒"] = new { units = new[] { "条", "包", "瓶", "盒", "罐" }, specs = new[] { "20支", "常规", "散装" } },
            ["饮料"] = new { units = new[] { "瓶", "罐", "盒", "箱", "包", "听" }, specs = new[] { "330ml", "500ml", "550ml", "250ml", "1L", "1.5L", "100g", "250g" } },
            ["零食"] = new { units = new[] { "包", "袋", "盒", "罐", "瓶" }, specs = new[] { "70g", "100g", "250g", "500g", "常规" } },
            ["调味"] = new { units = new[] { "瓶", "袋", "盒", "桶", "包" }, specs = new[] { "500ml", "250ml", "1L", "500g", "250g", "1kg" } },
            ["日用"] = new { units = new[] { "个", "瓶", "盒", "袋", "包", "块", "双", "张" }, specs = new[] { "常规", "115g", "200抽", "500ml", "250ml" } },
        });
        SetCfg("payMethods", new[] { "现金", "微信", "支付宝", "赊账" });

        await db.SaveChangesAsync();
    }

    private static UserInfo NewUser(string username, string name, string phone)
    {
        var salt = PasswordHasher.NewSalt();
        return new UserInfo
        {
            Username = username, Salt = salt, PasswordHash = PasswordHasher.Hash("123456", salt),
            Name = name, Phone = phone, CreatedAt = DateTime.Parse("2026-08-01 08:00"),
        };
    }
}
