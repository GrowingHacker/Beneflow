using Beneflow.Api.Services;
using Xunit;

namespace Beneflow.Tests.Unit;

/// <summary>
/// <see cref="StockMutationLock"/> 的契约测试。
///
/// 断言语义而不依赖墙钟时间：不去「睡一会儿再看结果」——那种写法在全量并行跑测试时
/// （几百个用例抢线程池，`Task.Delay` 的续体可能被推迟很久）会假失败。
/// 这里改用临界区并发计数与「不可能完成」的超时上界来表达不变式。
/// </summary>
public class StockMutationLockTests
{
    [Fact]
    public async Task 同一商品的锁_临界区同一时刻只允许一个操作()
    {
        var sut = new StockMutationLock();
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
        var sut = new StockMutationLock();

        using (await sut.AcquireAsync(new[] { 1 }))
        {
            // 商品 1 与商品 2 落在不同桶：应当立即取得，而不必等商品 1 释放。
            // 用「超时上界」表达：2 秒内必须拿到，否则说明被无关的商品挡住了。
            var acquireOther = sut.AcquireAsync(new[] { 2 });
            var finished = await Task.WhenAny(acquireOther, Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.True(finished == acquireOther, "不同商品之间不应互相阻塞");
            (await acquireOther).Dispose();
        }
    }

    [Fact]
    public async Task 多商品交叉加锁_不会死锁()
    {
        var sut = new StockMutationLock();

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
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(finished == both, "交叉加锁出现死锁：20 秒内未完成");
        await both;
    }

    [Fact]
    public async Task 空商品集合_为空操作不抛异常()
    {
        var sut = new StockMutationLock();

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
    public async Task 单据号锁_临界区同一时刻只允许一个操作()
    {
        var sut = new StockMutationLock();
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
}
