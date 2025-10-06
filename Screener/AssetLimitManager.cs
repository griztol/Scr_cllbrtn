using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Screener
{
    internal static class AssetLimitManager
    {
        /* Gravity: −1$ per hour if the limit exceeds the base value */
        private const double HourDecayUsd = 1;

        private static readonly ConcurrentDictionary<string, double> Limits = new ConcurrentDictionary<string, double>();
        private static readonly string CheckpointPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "limits.bin");

        /// <summary>Current coin limit (base value if no record).</summary>
        public static double GetLimit(string coin) =>
            Limits.GetOrAdd(coin, _ => GlbConst.StepUsd);

        /// <summary>Increases the limit linearly: Δ = amount × (Max-L)/(Max-Base).</summary>
        public static void IncreaseLinear(string coin, double amount)
        {
            if (amount <= 0 || GlbConst.workStopped) return;

            Limits.AddOrUpdate(
                coin,
                key => IncClamp(key, GlbConst.StepUsd, amount),          // new coin
                (key, cur) => IncClamp(key, cur, amount)             // record exists
            );
        }

        /// <summary>Decreases the limit by the specified amount (can go negative).</summary>
        public static void Decrease(string coin, double amount)
        {
            if (amount <= 0 || GlbConst.workStopped) return;

            Limits.AddOrUpdate(
                coin,
                key =>
                {
                    double updated = GlbConst.StepUsd - amount;
                    Logger.Add(key, $"Decrease −{amount}$ ⇒ {GlbConst.StepUsd:F2}$→{updated:F2}$", LogType.Action);
                    return updated;
                },
                (key, cur) =>
                {
                    double updated = cur - amount;
                    Logger.Add(key, $"Decrease −{amount}$ ⇒ {cur:F2}$→{updated:F2}$", LogType.Action);
                    return updated;
                });
        }

        public static async Task SaveLimitsAsync()
        {
            try
            {
                var snap = Limits.ToDictionary(kv => kv.Key, kv => kv.Value);
                await using var fs = File.Create(CheckpointPath);
                await JsonSerializer.SerializeAsync(fs, snap);
            }
            catch (Exception ex)
            {
                Logger.Add(null, "Limit save: " + ex.Message, LogType.Error);
            }
        }

        private static bool TryLoadLimits()
        {
            if (!File.Exists(CheckpointPath))
                return false;

            try
            {
                using var fs = File.OpenRead(CheckpointPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, double>>(fs);
                if (data is null) return false;

                foreach (var kv in data)
                    Limits[kv.Key] = kv.Value;

                return true;
            }
            catch (Exception ex)
            {
                Logger.Add(null, "Limit load: " + ex.Message, LogType.Error);
                return false;
            }
        }

        /* ------------------------- helpers ---------------------------------- */

        private static double IncClamp(string coin, double cur, double amount)
        {
            double coeff = Math.Clamp(
                (GlbConst.MaxLimitUsd - cur) / (GlbConst.MaxLimitUsd - GlbConst.StepUsd), 0, 1);

            double delta = amount * coeff;
            double updated = Math.Min(cur + delta, GlbConst.MaxLimitUsd);

            Logger.Add(coin,
                $"Increase +{amount}$ · f={coeff:F2} = +{delta:F2}$ ⇒ {cur:F2}$→{updated:F2}$",
                LogType.Action);

            return updated;
        }

        static AssetLimitManager()
        {
            TryLoadLimits();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(1));

                    // "Gravity":  −HourDecayUsd when the limit is above the base;
                    //              +HourDecayUsd when the limit is below the base.
                    foreach (var coin in Limits.Keys)          // ConcurrentDictionary: safe snapshot iteration
                    {
                        Limits.AddOrUpdate(
                            coin,
                            key => GlbConst.StepUsd,               // first time key? → base
                            (key, cur) =>
                            {
                                double updated = cur > GlbConst.StepUsd
                                    ? cur - HourDecayUsd        // pull down
                                    : cur + HourDecayUsd;       // pull up
                                return updated;
                            });
                    }

                }
            });
        }

    }

}