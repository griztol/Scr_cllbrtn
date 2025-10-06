using CryptoExchange.Net.Attributes;
using CryptoExchange.Net.Requests;
using Newtonsoft.Json;
using Scr_cllbrtn;
using Scr_cllbrtn.Exchanges;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Collections.Concurrent;

//var exc = new BitgetFt();

//await exc.RefreshMetadataAsync();

////var a = await exc.GetUnifiedTotalUsdtAsync();

//CurData c1 = await exc.GetLastPriceAsync("REDUSDT");
//Console.WriteLine(c1.askPrice);
//Console.WriteLine(c1.bidPrice);
//Console.WriteLine(c1.askAmount);
//Console.WriteLine(c1.bidAmount);


////await exc.UpdateBalanceAsync(c1);

////var v = await exc.GetAllCurrenciesAsync();
////Console.WriteLine(v.Count());

////Console.WriteLine(exc.totalUSDT);

//Task<OrderResult> rB = c1.BuyAsync(11.1m);
//Console.WriteLine("Done");
//await Task.Delay(9000);
//Console.ReadLine();

//await exc.CancelAllOrdersAsync(c1);

////Task<OrderResult> rB = c1.SellAsync(15, 0.3483011111m, noAlign: true);
////Task<OrderResult> rB = c1.SellAsync(2.7m, noAlign: true);
////await rB;


////await exc.CancelOrderAsync(rB.Result.orderId, c1.name);

//////if (!rB.Result.success)
//////{
//////    Console.WriteLine("errrrrrrrrrrrrror");
//////}

//////await Task.Delay(29000);

//await exc.CancelAllOrdersAsync(c1);


//Console.WriteLine("Done");
//await Task.Delay(5000);
//Console.ReadLine();
//return;




//while (true)
//{
//    // Measure buy time
//    var swBuy = Stopwatch.StartNew();
//    //var r = await c.BuyAsync(20, (decimal)c.bidPrice * 0.925m);
//    CurData c = await gg.GetLastPriceAsync("TRXUSDT");
//    swBuy.Stop();
//    Console.WriteLine($"[BuyAsync] Execution time: {swBuy.ElapsedMilliseconds} ms");

//    //// Wait for demonstration
//    //await Task.Delay(1000);

//    //// Measure cancel time
//    //var swCancel = Stopwatch.StartNew();
//    //var cancelResult = await gg.CancelOrderAsync(r.orderId, c.name);
//    //swCancel.Stop();
//    //Console.WriteLine($"[CancelOrderAsync] Execution time: {swCancel.ElapsedMilliseconds} ms");
//    Console.ReadLine();
//}




//// Assume GateFt and CurData are already imported

//var gg = new MexcSp();

//// Refresh metadata
//await gg.RefreshMetadataAsync();

//// Get pair data
//CurData c = await gg.GetLastPriceAsync("MILKUSDT");


//while (true)
//{

//    // Measure sell order send time
//    var swSell = Stopwatch.StartNew();
//    var r = await c.BuyAsync(30, 0.04870m, fok: true);
//    Console.WriteLine(r.success);
//    swSell.Stop();
//    Console.WriteLine($"[SellAsync] Execution time: {swSell.ElapsedMilliseconds} ms");

//    // Wait to see the output
//    Console.ReadLine();
//}
//return;


Keeper keeper = new Keeper();

ConcurrentDictionary<(int, int), HashSet<string>> commonCoins = new();

_ = Task.Run(async () =>
{
    while (true)
    {
        var black = GlbConst.ReadBlackListFromDesktop();

        foreach (var ex in GlbConst.ActiveEx)
        {
            try
            {
                await ex.RefreshMetadataAsync();
                ex.ApplyBlacklistToMeta(black);
            }
            catch (Exception e)
            {
                Logger.Add(null, $"{ex.GetType().Name} meta-refresh error: {e.Message}", LogType.Error);
            }
        }

        var tmp = new ConcurrentDictionary<(int, int), HashSet<string>>();
        for (int i = 0; i < GlbConst.ActiveEx.Length - 1; i++)
        {
            for (int j = i + 1; j < GlbConst.ActiveEx.Length; j++)
            {
                var set = GlbConst.ActiveEx[i].meta.Keys
                    .Intersect(GlbConst.ActiveEx[j].meta.Keys, StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (GlbConst.ActiveEx[j].exName.EndsWith("Ft", StringComparison.OrdinalIgnoreCase))
                    tmp[(i, j)] = set;
                if (GlbConst.ActiveEx[i].exName.EndsWith("Ft", StringComparison.OrdinalIgnoreCase))
                    tmp[(j, i)] = set;
            }
        }
        commonCoins = tmp;

        Logger.Add(null, $"Metadata refreshed; blacklist applied: {black.Count} symbols", LogType.Info);

        await Task.Delay(TimeSpan.FromMinutes(7));
    }
});

Thread.Sleep(19000);

foreach (var kv in commonCoins)
{
    var (i, j) = kv.Key;
    string ex1 = GlbConst.ActiveEx[i].exName;
    string ex2 = GlbConst.ActiveEx[j].exName;
    Console.WriteLine($"{ex1}, {ex2}, {kv.Value.Count}");
}

while (true)
{
    Thread.Sleep(900);
    Logger.Add(null, "------------------------- Opened deals = " + GlbConst.deals.Count, LogType.Info);

    List<Task<Dictionary<string, CurData>>> ans = new List<Task<Dictionary<string, CurData>>>();
    foreach (BaseExchange ex in GlbConst.ActiveEx)
    {
        ans.Add(ex.GetAllCurrenciesAsync());
        //ans.Add(TimeItAsync(() => ex.GetAllCurrenciesAsync(), $"GetAll {(string.IsNullOrEmpty(ex.exName) ? ex.GetType().Name : ex.exName)}"));
    }

    List<Task> tCompare = new();
    for (int i = 0; i < ans.Count; i++)
    {
        for (int j = 0; j < ans.Count; j++)
        {
            if (i == j) { continue; }

            if (commonCoins.TryGetValue((i, j), out var coins) && coins.Count > 0)
                tCompare.Add(CompareCurAsync(ans[i], ans[j], coins));
        }
    }

    await Task.WhenAll(tCompare);
}

async Task CompareCurAsync(Task<Dictionary<string, CurData>> t1, Task<Dictionary<string, CurData>> t2, HashSet<string> coins)
{
    //List<string> lines = new List<string>();
    await t1; await t2;
    //try { await t1; await t2; }
    //catch (Exception) 
    //{
    //    Logger.Add(null, "CompareCurAsync error", LogType.Error);
    //    return;
    //}

    bool needNewDeals = keeper.NeedNewDeals();

    if (t1.IsCanceled || t2.IsCanceled) { return; }

    var dict1 = t1.Result;
    var dict2 = t2.Result;

    foreach (var coin in coins)
    {
        if (!dict1.TryGetValue(coin, out var c1) || !dict2.TryGetValue(coin, out var c2))
            continue;
        if (c1.InBlackList || c2.InBlackList)
            continue;

        //lines.Add($"{c1.name} {c1.exchange} {c1.askPrice} {c1.askAmount} {c1.bidPrice} {c1.bidAmount} {c2.exchange} {c2.askPrice} {c2.askAmount} {c2.bidPrice} {c2.bidAmount}");

        double dIn = (c2.bidPrice / c1.askPrice) * 100 - 100;
        double dOut = (c1.bidPrice / c2.askPrice) * 100 - 100;

        if (needNewDeals)
        {
            if (c1.exchange.Contains("BitgetFt") || c2.exchange.Contains("BitgetFt")) { continue; }
            keeper.Inspect(c1, c2);
        }
    }

    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    string folderPath = Path.Combine(desktopPath, "data");
    string fileName = Path.Combine(folderPath, $"output_{DateTime.Now:yyyyMMdd_HHmmssfff}.txt");
    //if (GlbConst.SaveRawOutput) File.AppendAllLinesAsync(fileName, lines);
}




//static async Task<T> TimeItAsync<T>(Func<Task<T>> factory, string label)
//{
//    var sw = Stopwatch.StartNew();
//    try
//    {
//        return await factory().ConfigureAwait(false);
//    }
//    finally
//    {
//        sw.Stop();
//        if (!TryTpoArch(label, sw.Elapsed.TotalMilliseconds))
//            Logger.Add(null, $"[TIMING] {label}: {sw.Elapsed.TotalMilliseconds:F0} ms", LogType.Info);
//    }
//}

//static bool TryTpoArch(string label, double ms)
//{
//    try
//    {
//        var tpoType =
//            Type.GetType("TPoArch") ??
//            AppDomain.CurrentDomain.GetAssemblies()
//                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
//                .FirstOrDefault(t => t.Name == "TPoArch");

//        var m =
//            tpoType?.GetMethod("Timing", new[] { typeof(string), typeof(double) }) ??
//            tpoType?.GetMethod("Log", new[] { typeof(string), typeof(double) }) ??
//            tpoType?.GetMethod("Mark", new[] { typeof(string), typeof(double) }) ??
//            tpoType?.GetMethod("Write", new[] { typeof(string), typeof(double) });

//        if (m != null) { m.Invoke(null, new object[] { label, ms }); return true; }
//    }
//    catch { /* игнорируем */ }

//    return false;
//}