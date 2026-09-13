using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Tests;

/// <summary>
/// 采购进货单单元测试：创建、作废、编辑，及边界场景。
/// 重点验证：库存数量、移动加权平均成本、流水记录、作废标记。
/// </summary>
public class PurchaseServiceTests : TestBase
{
    // ========== 创建进货单 ==========

    [Fact]
    public async Task CreateAsync_SingleProduct_StockAndCostCorrect()
    {
        // 商品A：入库 10 件，进价 5 元
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var dto = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        };

        var result = await PurchaseSvc.CreateAsync(dto);

        Assert.Equal(0, result.Code);
        var order = GetPurchaseOrder(GetResultDataProp<int>(result.Data!, "id"));
        var p = GetProduct(productA.Id);

        Assert.Equal(10, p.StockQuantity);
        Assert.Equal(5.00m, p.CostPrice);
        Assert.Equal(50.00m, order.TotalAmount);
        Assert.Equal(10, order.TotalQty);
        Assert.False(order.IsVoided);
    }

    [Fact]
    public async Task CreateAsync_WeightedAverageCost_CalculatedCorrectly()
    {
        // 第一次入库：10 件 × 5 元 = 成本 5 元
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var dto1 = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        };
        await PurchaseSvc.CreateAsync(dto1);

        // 第二次入库：10 件 × 7 元
        // 移动加权平均 = (10×5 + 10×7) / 20 = 120/20 = 6 元
        var dto2 = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 7.00m }
            }
        };
        await PurchaseSvc.CreateAsync(dto2);

        var p = GetProduct(productA.Id);
        Assert.Equal(20, p.StockQuantity);
        Assert.Equal(6.00m, p.CostPrice);
    }

    [Fact]
    public async Task CreateAsync_StockLog_WrittenCorrectly()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var dto = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        };
        await PurchaseSvc.CreateAsync(dto);

        var log = LastStockLog(productA.Id);
        Assert.NotNull(log);
        Assert.Equal("采购入库", log!.ChangeType);
        Assert.Equal(10, log.ChangeQty);
        Assert.Equal(0, log.BeforeQty);
        Assert.Equal(10, log.AfterQty);
    }

    [Fact]
    public async Task CreateAsync_ExpiryProduct_BatchCreated()
    {
        // 商品B有有效期，入库后应生成批次
        var productB = Db.Products.First(p => p.Name.StartsWith("商品B"));
        var supplier = Db.Suppliers.First();

        var produceDate = new DateTime(2026, 1, 1);
        var dto = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productB.Id, Qty = 20, CostPrice = 8.00m, ProduceDate = produceDate }
            }
        };
        var result = await PurchaseSvc.CreateAsync(dto);
        var orderId = GetResultDataProp<int>(result.Data!, "id");

        var batches = Db.Batches.AsNoTracking().Where(b => b.ProductId == productB.Id).ToList();
        Assert.Single(batches);
        Assert.Equal(20, batches[0].Quantity);
        Assert.Equal(produceDate, batches[0].ProduceDate);
        Assert.Equal(produceDate.AddDays(productB.ShelfLifeDays), batches[0].ExpireDate);
        Assert.EndsWith(orderId.ToString("D4"), batches[0].BatchNo);
    }

    [Fact]
    public async Task CreateAsync_EmptyDetails_ReturnsError()
    {
        var supplier = Db.Suppliers.First();
        var dto = new CreatePurchaseDto { SupplierId = supplier.Id, Details = new List<PurchaseDetailDto>() };

        var result = await PurchaseSvc.CreateAsync(dto);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("请添加商品明细", result.Message);
    }

    [Fact]
    public async Task CreateAsync_InvalidSupplier_ReturnsError()
    {
        var dto = new CreatePurchaseDto
        {
            SupplierId = 9999,
            Details = new List<PurchaseDetailDto> { new() { ProductId = 1, Qty = 1, CostPrice = 1 } }
        };

        var result = await PurchaseSvc.CreateAsync(dto);

        Assert.NotEqual(0, result.Code);
        Assert.Contains("供应商不存在", result.Message);
    }

    // ========== 作废进货单 ==========

    [Fact]
    public async Task VoidAsync_NormalOrder_StockRolledBack()
    {
        // 先创建一单
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 作废
        var voidResult = await PurchaseSvc.VoidAsync(orderId);

        Assert.Equal(0, voidResult.Code);
        var order = GetPurchaseOrder(orderId);
        var p = GetProduct(productA.Id);

        Assert.True(order.IsVoided);
        Assert.NotNull(order.VoidedAt);
        Assert.Equal(0, p.StockQuantity);
    }

    [Fact]
    public async Task VoidAsync_CostReversed_Correctly()
    {
        // 第一次入库 10 件 × 5 元 → 成本 5
        // 第二次入库 10 件 × 7 元 → 成本 6
        // 作废第二次 → 应回到成本 5，库存 10
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });

        var create2 = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 7.00m }
            }
        });
        var order2Id = GetResultDataProp<int>(create2.Data!, "id");

        // 作废第二单
        await PurchaseSvc.VoidAsync(order2Id);

        var p = GetProduct(productA.Id);
        Assert.Equal(10, p.StockQuantity);
        Assert.Equal(5.00m, p.CostPrice);
    }

    [Fact]
    public async Task VoidAsync_StockLog_Written()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        await PurchaseSvc.VoidAsync(orderId);

        var log = LastStockLog(productA.Id);
        Assert.NotNull(log);
        Assert.Equal("作废回退", log!.ChangeType);
        Assert.Equal(-10, log.ChangeQty);
        Assert.Equal(10, log.BeforeQty);
        Assert.Equal(0, log.AfterQty);
    }

    [Fact]
    public async Task VoidAsync_AlreadyVoided_ReturnsError()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        await PurchaseSvc.VoidAsync(orderId);
        var result2 = await PurchaseSvc.VoidAsync(orderId);

        Assert.NotEqual(0, result2.Code);
        Assert.Contains("已作废", result2.Message);
    }

    [Fact]
    public async Task VoidAsync_ExpiryProduct_BatchRemoved()
    {
        var productB = Db.Products.First(p => p.Name.StartsWith("商品B"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productB.Id, Qty = 20, CostPrice = 8.00m, ProduceDate = new DateTime(2026, 1, 1) }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 作废前有批次
        Assert.NotEmpty(Db.Batches.Where(b => b.ProductId == productB.Id));

        await PurchaseSvc.VoidAsync(orderId);

        // 作废后批次被删除
        Assert.Empty(Db.Batches.Where(b => b.ProductId == productB.Id));
    }

    [Fact]
    public async Task VoidAsync_OperationLog_Written()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        await PurchaseSvc.VoidAsync(orderId);

        var log = Db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .First(l => l.Module == "采购管理");

        Assert.Equal("作废进货单", log.Action);
    }

    // ========== 编辑进货单 ==========

    [Fact]
    public async Task UpdateAsync_ChangeQty_StockAdjusted()
    {
        // 创建：10 件 × 5 元
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 编辑：改成 20 件 × 5 元
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 20, CostPrice = 5.00m }
            }
        });

        Assert.Equal(0, updateResult.Code);
        var p = GetProduct(productA.Id);
        Assert.Equal(20, p.StockQuantity);
        Assert.Equal(5.00m, p.CostPrice); // 进价没变，成本不变
    }

    [Fact]
    public async Task UpdateAsync_ChangeCostPrice_CostRecalculated()
    {
        // 创建：10 件 × 5 元 → 成本 5
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 编辑：改成 10 件 × 7 元 → 成本应变成 7
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 7.00m }
            }
        });

        Assert.Equal(0, updateResult.Code);
        var p = GetProduct(productA.Id);
        Assert.Equal(10, p.StockQuantity);
        Assert.Equal(7.00m, p.CostPrice);
    }

    [Fact]
    public async Task UpdateAsync_AddProduct_StockIncreased()
    {
        // 创建：只有商品A 10件
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var productB = Db.Products.First(p => p.Name.StartsWith("商品B"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 编辑：加上商品B
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m },
                new() { ProductId = productB.Id, Qty = 5, CostPrice = 8.00m, ProduceDate = new DateTime(2026,1,1) }
            }
        });

        Assert.Equal(0, updateResult.Code);
        Assert.Equal(10, GetProduct(productA.Id).StockQuantity);
        Assert.Equal(5, GetProduct(productB.Id).StockQuantity);

        var details = GetPurchaseDetails(orderId);
        Assert.Equal(2, details.Count);
    }

    [Fact]
    public async Task UpdateAsync_RemoveProduct_StockDecreased()
    {
        // 创建：商品A + 商品B
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var productB = Db.Products.First(p => p.Name.StartsWith("商品B"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m },
                new() { ProductId = productB.Id, Qty = 5, CostPrice = 8.00m, ProduceDate = new DateTime(2026,1,1) }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 编辑：去掉商品B，只保留商品A
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });

        Assert.Equal(0, updateResult.Code);
        Assert.Equal(10, GetProduct(productA.Id).StockQuantity);
        Assert.Equal(0, GetProduct(productB.Id).StockQuantity);
    }

    [Fact]
    public async Task UpdateAsync_OrderNoAndCreatedAt_Unchanged()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");
        var oldOrder = GetPurchaseOrder(orderId);

        // 编辑
        await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 20, CostPrice = 6.00m }
            }
        });

        var newOrder = GetPurchaseOrder(orderId);
        Assert.Equal(oldOrder.OrderNo, newOrder.OrderNo);
        Assert.Equal(oldOrder.CreatedAt, newOrder.CreatedAt);
    }

    [Fact]
    public async Task UpdateAsync_AlreadyVoided_ReturnsError()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        await PurchaseSvc.VoidAsync(orderId);

        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 20, CostPrice = 5.00m }
            }
        });

        Assert.NotEqual(0, updateResult.Code);
        Assert.Contains("已作废", updateResult.Message);
    }

    // ========== 边界场景 ==========

    [Fact]
    public async Task VoidAsync_InsufficientStock_ReturnsError()
    {
        // 入库 10 件，然后手动把库存改成 5（模拟已卖出 5 件）
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 模拟销售：库存减到 5
        var p = Db.Products.First(p => p.Id == productA.Id);
        p.StockQuantity = 5;
        await Db.SaveChangesAsync();

        // 作废应该失败（库存不足回退）
        var voidResult = await PurchaseSvc.VoidAsync(orderId);

        Assert.NotEqual(0, voidResult.Code);
        Assert.Contains("不足", voidResult.Message);
    }

    [Fact]
    public async Task UpdateAsync_InsufficientStock_ReturnsError()
    {
        // 入库 10 件
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 模拟销售：库存减到 3
        var p = Db.Products.First(p => p.Id == productA.Id);
        p.StockQuantity = 3;
        await Db.SaveChangesAsync();

        // 编辑时改成 5 件（需要先回退 10 件，但库存只有 3，不够）
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 5, CostPrice = 5.00m }
            }
        });

        Assert.NotEqual(0, updateResult.Code);
        Assert.Contains("不足", updateResult.Message);
    }

    [Fact]
    public async Task CreateAsync_MultipleSameProducts_Aggregated()
    {
        // 同一商品加两行，应合并为一行
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier = Db.Suppliers.First();

        var dto = new CreatePurchaseDto
        {
            SupplierId = supplier.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 5, CostPrice = 5.00m },
                new() { ProductId = productA.Id, Qty = 5, CostPrice = 5.00m }
            }
        };

        var result = await PurchaseSvc.CreateAsync(dto);
        var orderId = GetResultDataProp<int>(result.Data!, "id");
        var details = GetPurchaseDetails(orderId);

        Assert.Single(details);
        Assert.Equal(10, details[0].Qty);

        var p = GetProduct(productA.Id);
        Assert.Equal(10, p.StockQuantity);
    }

    [Fact]
    public async Task UpdateAsync_ChangeSupplier_Successful()
    {
        var productA = Db.Products.First(p => p.Name.StartsWith("商品A"));
        var supplier1 = Db.Suppliers.First();

        // 新增第二个供应商
        var supplier2 = new Supplier { Name = "供应商2号", Contact = "李四", Phone = "13900139000" };
        Db.Suppliers.Add(supplier2);
        await Db.SaveChangesAsync();

        var createResult = await PurchaseSvc.CreateAsync(new CreatePurchaseDto
        {
            SupplierId = supplier1.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });
        var orderId = GetResultDataProp<int>(createResult.Data!, "id");

        // 改供应商
        var updateResult = await PurchaseSvc.UpdateAsync(orderId, new CreatePurchaseDto
        {
            SupplierId = supplier2.Id,
            Details = new List<PurchaseDetailDto>
            {
                new() { ProductId = productA.Id, Qty = 10, CostPrice = 5.00m }
            }
        });

        Assert.Equal(0, updateResult.Code);
        var order = GetPurchaseOrder(orderId);
        Assert.Equal(supplier2.Id, order.SupplierId);
    }
}

