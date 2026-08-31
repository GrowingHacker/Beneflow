using System.Text.Json;
using Beneflow.Api.Data;
using Beneflow.Api.Models;
using Beneflow.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Beneflow.Api.Services;

/// <summary>供应商管理：列表（含累计采购/最近供货）/ 详情 / 增改删（已有采购记录不可删，仅可停用）</summary>
public class SupplierService : ISupplierService
{
    private readonly AppDbContext _db;
    public SupplierService(AppDbContext db) => _db = db;

    public async Task<PagedResult<object>> ListAsync(string? keyword, int page, int pageSize)
    {
        var q =
            from s in _db.Suppliers.AsNoTracking()
            where string.IsNullOrEmpty(keyword) || s.Name.Contains(keyword!) || s.Contact!.Contains(keyword)
            select new
            {
                s.Id, s.Name, s.Contact, s.Phone, s.Address, s.Remark, s.Status,
                TotalAmount = _db.PurchaseOrders.Where(po => po.SupplierId == s.Id).Sum(po => (decimal?)po.TotalAmount) ?? 0,
                LastDate = _db.PurchaseOrders.Where(po => po.SupplierId == s.Id).Max(po => (DateTime?)po.CreatedAt),
                HasPurchase = _db.PurchaseOrders.Any(po => po.SupplierId == s.Id),
            };
        var total = await q.CountAsync();
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        page = Math.Max(1, page);
        var rows = await q.OrderBy(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var list = rows.Select(s => (object)new
        {
            id = s.Id, name = s.Name, contact = s.Contact, phone = s.Phone,
            address = s.Address, remark = s.Remark ?? "", status = s.Status ? "启用" : "停用",
            totalAmount = Math.Round(s.TotalAmount, 2),          // 累计采购金额
            lastDate = s.LastDate?.ToString("yyyy-MM-dd"),       // 最近供货时间
            hasPurchase = s.HasPurchase,                         // 是否已有采购记录（决定能否删除）
            createdAt = "",
        }).ToList();
        return new PagedResult<object> { List = list, Total = total, Page = page, PageSize = pageSize };
    }

    public async Task<ApiResult<object>> GetAsync(int id)
    {
        var s = await _db.Suppliers.FindAsync(id);
        if (s == null) return ApiResult<object>.Fail("供应商不存在");
        return ApiResult<object>.Ok(new { id = s.Id, name = s.Name, contact = s.Contact, phone = s.Phone, address = s.Address, remark = s.Remark, status = s.Status ? "启用" : "停用" });
    }

    public async Task<ApiResult<object>> CreateAsync(SupplierUpsertDto dto)
    {
        dto.Name = dto.Name?.Trim() ?? "";
        if (dto.Name.Length == 0) return ApiResult<object>.Fail("请填写供应商名称");
        if (await _db.Suppliers.AnyAsync(s => s.Name == dto.Name)) return ApiResult<object>.Fail("供应商名称已存在");
        var s = new Supplier { Name = dto.Name, Contact = dto.Contact, Phone = dto.Phone, Address = dto.Address, Remark = dto.Remark, Status = dto.Status };
        _db.Suppliers.Add(s);
        await _db.SaveChangesAsync();
        return ApiResult<object>.Ok(new { id = s.Id });
    }

    public async Task<ApiResult> UpdateAsync(int id, JsonElement body)
    {
        var s = await _db.Suppliers.FindAsync(id);
        if (s == null) return ApiResult.Fail("供应商不存在");

        // 兼容两种提交：完整表单 或 仅 { status } 启停开关；支持字符串 "启用"/"停用" 或 bool true/false
        if (body.TryGetProperty("status", out var stEl))
        {
            if (stEl.ValueKind == JsonValueKind.True || stEl.ValueKind == JsonValueKind.False)
                s.Status = stEl.GetBoolean();
            else
            {
                var st = stEl.GetString();
                if (!string.IsNullOrEmpty(st)) s.Status = st == "启用";
            }
        }
        if (body.TryGetProperty("name", out var nEl) && !string.IsNullOrWhiteSpace(nEl.GetString()))
        {
            var newName = nEl.GetString()!.Trim();
            if (await _db.Suppliers.AnyAsync(x => x.Id != id && x.Name == newName))
                return ApiResult.Fail("供应商名称已存在");
            s.Name = newName;
        }
        if (body.TryGetProperty("contact", out var cEl)) s.Contact = cEl.GetString();
        if (body.TryGetProperty("phone", out var pEl)) s.Phone = pEl.GetString();
        if (body.TryGetProperty("address", out var aEl)) s.Address = aEl.GetString();
        if (body.TryGetProperty("remark", out var rEl)) s.Remark = rEl.GetString();

        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(int id)
    {
        var s = await _db.Suppliers.FindAsync(id);
        if (s == null) return ApiResult.Fail("供应商不存在");

        // 已有采购记录的供应商不可删除（进销存留痕），提示改为停用
        if (await _db.PurchaseOrders.AnyAsync(po => po.SupplierId == id))
            return ApiResult.Fail("该供应商已有采购记录，不能删除，可改为停用");

        _db.Suppliers.Remove(s);
        await _db.SaveChangesAsync();
        return ApiResult.Ok();
    }
}
