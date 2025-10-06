using Scr_cllbrtn.Exchanges;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Formats.Asn1;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scr_cllbrtn
{
    public static class GlbConst
    {
        public const int MaxOpenedDeals = 2;
        public const double DeltaInOut = 0.99;
        public static volatile int WaitingTime = 30;

        //public const decimal BaseLimitUsd = 9;
        public const double MaxLimitUsd = 31;
        public const double StepUsd = 30;
        public static bool workStopped = false;

        public static double? totalUsdtFloor = null;
        static readonly object totalUsdtFloorLock = new();

        public const bool SaveRawOutput = false;

        //public static ConcurrentDictionary<string, decimal> stepQnt = new ConcurrentDictionary<string, decimal>();
        //public static HashSet<string> blackList = File.ReadAllLines(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\BlackList.txt").ToHashSet();
        public static List<DealCloser> deals = new List<DealCloser>();
        public static readonly ImmutableArray<BaseExchange> ActiveEx =
        [
            new BingxFt(),
            //new BingxSp(),
            new BitgetFt(),
            //new BitgetSp(),
            //new BinanceSp(),
            //new BybitFt(),
            new MexcSp(),
            new GateFt(),
            new GateSp(),
            //new HtxFt(),
            //new HtxSp(),
            //new KucoinFt(),
            //new KucoinSp(),
            //new OkxFt(),
            //new OkxSp(),

            //new MexcFt(),
            //new PoloniexSp(),
        ];

        public static void StopWork()
        {
            File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "stop_100.txt"), "");
            Logger.Add(null, "Work stopped", LogType.Info);
        }

        public static void CheckUsdtFloor(double deadBand = 2.0)
        {
            var balances = ActiveEx
                .Select(exchange => (exchange, total: exchange.TotalUsdt))
                .ToArray();

            foreach (var (exchange, total) in balances)
            {
                Logger.Add(null, $"{exchange.exName} USDT balance = {total:F2}", LogType.Info);
            }

            var ready = balances.Where(b => b.total > 0).ToArray();
            if (ready.Length == 0)
            {
                Logger.Add(null, "USDT floor check skipped: no exchanges with balance", LogType.Error);
                StopWork();
                return;
            }

            double sum = ready.Sum(b => b.total);

            lock (totalUsdtFloorLock)
            {
                if (totalUsdtFloor is null)
                {
                    totalUsdtFloor = sum - deadBand;
                    Logger.Add(null, $"Init USDT floor = {totalUsdtFloor:F2}", LogType.Info);
                    return;
                }

                if (sum < totalUsdtFloor)
                {
                    Logger.Add(null, $"TotalUSDT balance {sum:F2} < floor {totalUsdtFloor:F2}", LogType.Error);
                    StopWork();
                    return;
                }

                if (sum > totalUsdtFloor + deadBand)
                {
                    totalUsdtFloor = sum - deadBand;
                    Logger.Add(null, $"Update USDT floor = {totalUsdtFloor:F2}", LogType.Info);
                }
            }
        }

        public static HashSet<string> ReadBlackListFromDesktop()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BlackList.txt");
                if (!File.Exists(path))
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var set = File.ReadAllLines(path)
                              .Select(s => s.Split('#')[0].Trim())
                              .Where(s => !string.IsNullOrWhiteSpace(s))
                              .Select(s => s.ToUpperInvariant())
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return set;
            }
            catch (Exception e)
            {
                Logger.Add(null, "Blacklist read error: " + e.Message, LogType.Error);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

    }
}
