using Beneflow.Api.Services;
using Xunit;

namespace Beneflow.Tests.Unit;

/// <summary>
/// <see cref="StockMutationLock"/> 的契约测试。
///
/// <para>
/// <b>两条纪律，缺一就会让整套测试挂死：</b>
/// </para>
/// <para>
/// ① <b>只断定语义，不依赖墙钟时间</b>。不去「睡一会儿再看结果」——全量并行跑测试时
/// （几百个用例抢线程池，`Task.Delay` 的续体可能被推迟很久）那种写法会假失败。
/// 这里用「<c>WaitAsync</c> 能不能<b>同步</b>完成」来表达：无竞争的桶必然同步完成
/// （<c>IsCompletedSuccessfully</c>），已被占住的桶必然做不到同步完成（<c>!IsCompleted</c>）。
/// 两者都是瞬时判断，与机器负载无关。
/// </para>
/// <para>
/// ② <b>一律用 <see cref="StockMutationLock.CreateIsolated"/>，不要 new 默认构造</b>。
/// 默认构造的互斥量是<b>进程内静态共享</b>的，而集成测试会跑真实服务去用同一批桶。
/// 单元测试若也去抢共享桶，不但会被无关用例拖慢，更糟的是：断言一旦失败（在 await 到一半就抛），
/// 已经拿到的桶就再也回不来，之后凡是碰到该桶的用例全部挂死 —— 一次偶发抖动升级成整套挂起。
/// 隔离实例让每个用例拥有自己的桶，失败也只是一条用例失败。
/// </para>
/// </summary>
public class StockMutationLockTests
{
    /// <summary>
    /// 默认构造专用：挑一个大 ID，让它落到靠后的桶（<c>999999 % 64 = 63</c>），
    /// 尽量避开集成测试用的那批小 ID。
    /// </summary>
    private const int SharedProbeProductId = 999999;

    [Fact]
    public async Task 同一商品的锁_临界区同一时刻只允许一个操作()
    {
        var sut = StockMutationLock.CreateIsolated();
        var active = 0;          // 当前处于临界区内的操作数
        var maxActive = 0;       // 观测到的最大值
        var gate = new object();

        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            using (await sut.AcquireAsync(new[] { 7 }))
            {
                var now = Interlocked.Increment(ref active);
                lock (gate) { if (now > maxActive) maxActive = now; }
                await Task.Delay(5);
                Interlocked.Decrement(ref active);
            }
        });

        await Task.WhenAll(tasks);

        // 锁生效时这个值恒为 1；若互斥失效，16 个任务并发进入几乎是必然的事
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task 不同商品的锁_互不阻塞()
    {
        var sut = StockMutationLock.CreateIsolated();

        using (await sut.AcquireAsync(new[] { 1 }))
        {
            // 商品 1 与商品 2 落在不同桶：这次获取不应当被商品 1 挡住。
            // 用「是否同步完成」表达，而不是「几秒内完成」——
            // 无竞争的 SemaphoreSlim.WaitAsync 必然同步返回，因此这是精确判断，
            // 也不受并行测试的线程池调度影响。
            var acquireOther = sut.AcquireAsync(new[] { 2 });
            Assert.True(acquireOther.IsCompletedSuccessfully, "不同商品之间不应互相阻塞");
            (await acquireOther).Dispose();
        }
    }

    [Fact]
    public async Task 多商品交叉加锁_不会死锁()
    {
        var sut = StockMutationLock.CreateIsolated();

        // 两边以相反的入参顺序申请同一组商品。若实现按入参顺序逐个加锁，就会形成死锁环；
        // 实现里按桶序号升序获取、逆序释放，因此必须能在超时前跑完。
        var a = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
                using (await sut.AcquireAsync(new[] { 1, 2, 3 })) { }
        });

        var b = Task.Run(async () =>
        {
            for (var i = 0; i < 50; i++)
                using (await sut.AcquireAsync(new[] { 3, 2, 1 })) { }
        });

        var both = Task.WhenAll(a, b);
        // 循环体里没有任何等待，只有取锁/放锁，正常应在微秒级跑完；20 秒是六个数量级的余量。
        // 「死锁」这个性质本身只能靠「等不出来」来证明，所以这一条保留超时上界。
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(finished == both, "交叉加锁出现死锁：20 秒内未完成");
        await both;
    }

    [Fact]
    public async Task 空商品集合_为空操作不抛异常()
    {
        var sut = StockMutationLock.CreateIsolated();

        // 「订单不存在 / 查不到明细」等场景会传空集合，必须安全
        using (await sut.AcquireAsync(Array.Empty<int>()))
        {
        }

        // 商品 ID 为 0 也要落到合法桶
        using (await sut.AcquireAsync(new[] { 0 }))
        {
        }
    }

    [Fact]
    public async Task 释放后_同一个桶可以再次获取()
    {
        var sut = StockMutationLock.CreateIsolated();

        using (await sut.AcquireAsync(new[] { 5 })) { }

        // 释放若有漏（少放一次、放错桶、重复放），这里就会拿不到而同步失败。
        var again = sut.AcquireAsync(new[] { 5 });
        Assert.True(again.IsCompletedSuccessfully, "锁释放后应当能立即重新获取");
        (await again).Dispose();
    }

    [Fact]
    public async Task 单据号锁_临界区同一时刻只允许一个操作()
    {
        var sut = StockMutationLock.CreateIsolated();
        var active = 0;
        var maxActive = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            using (await sut.AcquireOrderNoAsync())
            {
                var now = Interlocked.Increment(ref active);
                lock (gate) { if (now > maxActive) maxActive = now; }
                await Task.Delay(5);
                Interlocked.Decrement(ref active);
            }
        });

        await Task.WhenAll(tasks);

        // 建单区间必须完全串行，否则并发请求会算出同一个单号
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task 默认构造_实例之间共享互斥量()
    {
        // 「跨 DI 作用域互斥」这个性质来自互斥量是静态共享的，而不来自锁被注册成单例。
        // 一旦有人把静态字段改回实例字段，互斥会在并发请求之间静默失效（丢更新、库存负数），
        // 而所有只测单个实例的用例都发现不了。这条专门把这个前提钉住。
        var a = new StockMutationLock();
        var b = new StockMutationLock();

        var held = await a.AcquireAsync(new[] { SharedProbeProductId });
        var second = b.AcquireAsync(new[] { SharedProbeProductId });
        try
        {
            // 桶已被 a 占住 ⇒ b 的等待不可能同步完成。全程不睡眠、不等超时。
            Assert.False(second.IsCompleted, "不同实例之间必须共享同一组互斥量（否则跨请求互斥会静默失效）");
        }
        finally
        {
            // 无论断言成败都必须把桶还回去：先放 a 的锁，再把 b 那份收下并释放。
            held.Dispose();
            using (await second) { }
        }
    }
}
