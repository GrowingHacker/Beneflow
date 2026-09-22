namespace Beneflow.Api.Services;

/// <summary>
/// 库存变更互斥锁：把「读库存 → 校验 → 修改 → 提交」整段临界区串行化，消除丢更新。
///
/// <para>
/// 背景：销售 / 进货 / 退货 / 盘点都会执行「先读 StockQuantity，判断够不够，再写回新值」。
/// 在 SQL Server 默认的 ReadCommitted 隔离级别下，SELECT 不持有排他锁，两个并发请求可以
/// 读到同一个旧值、同时通过校验、再各自写回——后写的那次覆盖先写的（lost update）。
/// 典型后果是库存被卖成负数，或退货回补凭空丢失。
/// </para>
///
/// <para>
/// 方案：按商品 ID 分桶（固定 64 个桶）取互斥量。同一商品的操作串行，不同商品互不阻塞。
/// 一次涉及多个商品时，按桶序号升序获取、降序释放，避免两个请求交叉持锁造成死锁。
/// </para>
///
/// <para>
/// <b>适用边界</b>：本方案依赖「单进程部署」——本项目生产形态是 Kestrel + Windows Service
/// 单实例，满足该前提。若将来改为多实例 / 多进程部署，进程内锁会失效，届时需改为数据库层
/// 方案（<c>Product</c> 加 RowVersion 乐观并发，或把扣减改成带
/// <c>WHERE StockQuantity &gt;= qty</c> 的条件更新并校验受影响行数）。
/// </para>
/// </summary>
public sealed class StockMutationLock
{
    private const int BucketCount = 64;

    // 静态字段：保证互斥量在不同 DI 作用域（不同请求）之间共享。
    private static readonly SemaphoreSlim[] SharedBuckets = CreateBuckets(BucketCount);

    // 单据号分配专用锁。单号形如 {前缀}{yyyyMMdd}{当日序号}，序号由「今日单据数」推导，
    // 并发下两个请求会算出同一个序号，撞 OrderNo 唯一索引后抛异常返回 500。
    // 由于序号来自已提交的行数，只在「算号」这一瞬间加锁并不够——必须把
    // 「算号 → 落库提交」整段一起串行，因此这把锁会覆盖整个建单过程。
    // 便利店单机收银的写入量极低，串行建单没有性能问题。
    private static readonly SemaphoreSlim SharedOrderNoGate = new(1, 1);

    private readonly SemaphoreSlim[] _buckets;
    private readonly SemaphoreSlim _orderNoGate;

    /// <summary>
    /// 生产用构造：<b>所有实例共享同一组互斥量</b>，这正是「跨 DI 作用域互斥」的来源
    /// （锁注册为单例；即使将来被改成作用域内实例，共享互斥量也能继续保证互斥）。
    /// </summary>
    public StockMutationLock() : this(SharedBuckets, SharedOrderNoGate) { }

    private StockMutationLock(SemaphoreSlim[] buckets, SemaphoreSlim orderNoGate)
    {
        _buckets = buckets;
        _orderNoGate = orderNoGate;
    }

    /// <summary>
    /// <b>测试专用</b>：造一个自带一组独立互斥量的实例。
    ///
    /// <para>
    /// 单元测试必须用它，而不是 <c>new StockMutationLock()</c> —— 后者的互斥量是进程内共享的，
    /// 一旦某个用例断言失败（例如在 <c>await</c> 到一半就抛，来不及 Dispose），
    /// 就会把某个桶永久占住；之后所有用到该桶的用例（含跑真实服务的集成测试）全部挂死。
    /// 一次偶发抖动会被升级成整套测试挂起，且现场几乎无法定位。
    /// </para>
    /// </summary>
    internal static StockMutationLock CreateIsolated(int bucketCount = BucketCount)
        => new(CreateBuckets(bucketCount), new SemaphoreSlim(1, 1));

    private static SemaphoreSlim[] CreateBuckets(int count)
    {
        var arr = new SemaphoreSlim[count];
        for (var i = 0; i < count; i++) arr[i] = new SemaphoreSlim(1, 1);
        return arr;
    }

    /// <summary>商品 ID → 桶序号（负数也能落到合法区间）。</summary>
    private int BucketOf(int productId) => (productId & int.MaxValue) % _buckets.Length;

    /// <summary>
    /// 获取「单据号分配」全局锁。<b>必须先于 <see cref="AcquireAsync"/> 获取</b>，
    /// 保持全局统一的加锁顺序，避免与其他请求构成死锁环。
    /// </summary>
    public async Task<IDisposable> AcquireOrderNoAsync()
    {
        await _orderNoGate.WaitAsync();
        return new OrderNoReleaser(_orderNoGate);
    }

    /// <summary>
    /// 对给定商品集合加锁。请用 <c>using (await _stockLock.AcquireAsync(ids)) { ... }</c> 包裹
    /// 整个「读-校验-改-提交」临界区；释放即在 Dispose 时。
    /// 传入空集合时为空操作（用于「查不到明细」等情形）。
    /// </summary>
    public async Task<IDisposable> AcquireAsync(IEnumerable<int> productIds)
    {
        var buckets = _buckets;
        var indexes = productIds.Select(BucketOf).Distinct().OrderBy(i => i).ToArray();
        var held = 0;
        try
        {
            for (; held < indexes.Length; held++)
                await buckets[indexes[held]].WaitAsync();
        }
        catch
        {
            // 获取过程中异常/取消：回滚已持有的锁，避免把桶永久占住
            for (var i = held - 1; i >= 0; i--) buckets[indexes[i]].Release();
            throw;
        }

        return new Releaser(buckets, indexes);
    }

    private sealed class OrderNoReleaser(SemaphoreSlim gate) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            gate.Release();
        }
    }

    private sealed class Releaser(SemaphoreSlim[] buckets, int[] indexes) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 逆序释放，与获取顺序相反
            for (var i = indexes.Length - 1; i >= 0; i--) buckets[indexes[i]].Release();
        }
    }
}
