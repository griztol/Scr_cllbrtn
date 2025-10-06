using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Screener
{
    public static class InOutPercent
    {
        // Range where the optimal exit delta is searched
        private const double DeltaMin = -1.8;
        private const double DeltaMax = 1.8;
        // Distribution segment step
        private const double DeltaStep = 0.3;
        private const int WindowSize = 25000;
        private const double HistStepValue = 100.0 / WindowSize;

        private static readonly string CheckpointPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "histograms.bin");

        // Number of segments in the histogram
        private static readonly int HistSteps = (int)Math.Round((DeltaMax - DeltaMin) / DeltaStep);

        //private const string InputDirectory = @"C:\Users\Administrator\Desktop\data";
        private static readonly string InputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "data");

        private static readonly ConcurrentDictionary<(string buyEx, string sellEx, string coin), ConcurrentQueue<int>> coinData
            = new();

        private static readonly ConcurrentDictionary<(string buyEx, string sellEx, string coin), double[]> coinHistograms
            = new();

        private record HistState(Dictionary<string, int[]> CoinQueues, Dictionary<string, double[]> CoinHists);

        public static void LoadInitialData()
        {
            if (TryLoadCheckpoint())
                return;

            // Search for all "output_*.txt" files
            var inputFiles = Directory.GetFiles(InputDirectory, "output_*.txt");
            // Sort so that we go from the earliest to the latest
            Array.Sort(inputFiles);

            int totalFiles = inputFiles.Length;
            // If there are more files than the window, skip the old ones and take the last _windowSize
            if (totalFiles > WindowSize)
            {
                inputFiles = inputFiles.Skip(totalFiles - WindowSize).ToArray();
            }

            // Now process the filtered files
            int processed = 0;
            foreach (var file in inputFiles)
            {
                // Read lines from the file
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                        continue;

                    string symbol = parts[0];
                    string buyEx = parts[1];
                    string sellEx = parts[6];

                    // Try to extract numeric values (valueA, valueB, valueC, valueD)
                    if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double valueA) ||
                        !double.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out double valueB) ||
                        !double.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out double valueC) ||
                        !double.TryParse(parts[9], NumberStyles.Any, CultureInfo.InvariantCulture, out double valueD))
                    {
                        continue;
                    }

                    // Calculation logic like in your example
                    double x1 = (valueD / valueA) * 100 - 100;  // inPrc
                    double x2 = (valueB / valueC) * 100 - 100;  // outPrc

                    // Add to our structures
                    AddRecord(buyEx, sellEx, symbol, x1, x2);
                }

                processed++;
                Console.WriteLine($"Processed file {processed} of {inputFiles.Length}: {file}");
            }

        }

        public static void AddRecord(CurData buy, CurData sell, double inPrc, double outPrc)
            => AddRecord(buy.exchange, sell.exchange, buy.name, inPrc, outPrc);

        private static void AddRecord(string buyEx, string sellEx, string coin, double inPrc, double outPrc)
        {
            var key = (buyEx, sellEx, coin);
            var queue = coinData.GetOrAdd(key, _ => new ConcurrentQueue<int>());

            int index = GetHistIndex(outPrc);
            queue.Enqueue(index);

            var hist = coinHistograms.GetOrAdd(key, _ => new double[HistSteps + 1]);

            if (index != -1)
            {
                UpdateHist(hist, index, 1);
            }

            if (queue.Count > WindowSize && queue.TryDequeue(out var oldIndex))
            {
                if (oldIndex != -1)
                    UpdateHist(hist, oldIndex, -1);
            }
        }

        private static int GetHistIndex(double value)
        {
            if (value < DeltaMin || value > DeltaMax) return -1;
            int index = (int)Math.Round((value - DeltaMin) / DeltaStep);
            if (index < 0 || index > HistSteps) return -1;
            return index;
        }

        private static void UpdateHist(double[] hist, int index, int direction)
        {
            if (index < 0 || index > HistSteps) return;
            hist[index] += direction * HistStepValue;
            if (hist[index] < 0) hist[index] = 0;
        }

        public static (double inPrc, double outPrc) GetCurrentThresholds(CurData buy, CurData sell)
        {
            //return (1.5, 0);
            var key = (buy.exchange, sell.exchange, buy.name);
            if (!coinHistograms.TryGetValue(key, out var hist) || hist.Length == 0)
            {
                return ((double)decimal.MaxValue, (double)decimal.MaxValue);
            }

            if (hist.Sum() < 70)
            {
                return ((double)decimal.MaxValue, (double)decimal.MaxValue);
            }

            int threshIndex = -1;
            for (int i = hist.Length - 1; i >= 0; i--)
            {
                if (hist[i] > 10)
                {
                    threshIndex = i;
                    break;
                }
            }

            if (threshIndex == -1)
            {
                return ((double)decimal.MaxValue, (double)decimal.MaxValue);
            }

            double bDbl = DeltaMin + threshIndex * DeltaStep;


            return (GlbConst.DeltaInOut - bDbl - (buy.FundingRate < 0 ? buy.FundingRate * 2 : 0), bDbl);
        }

        public static async Task SaveCheckpointAsync()
        {
            try
            {
                var snapQueues = coinData.ToDictionary(
                    kv => $"{kv.Key.buyEx}|{kv.Key.sellEx}|{kv.Key.coin}",
                    kv => kv.Value.ToArray());
                var snapHists = coinHistograms.ToDictionary(
                    kv => $"{kv.Key.buyEx}|{kv.Key.sellEx}|{kv.Key.coin}",
                    kv => kv.Value);

                await using var fs = File.Create(CheckpointPath);
                await JsonSerializer.SerializeAsync(fs, new HistState(snapQueues, snapHists));
                fs.Close();
            }
            catch (Exception ex)
            {
                Logger.Add(null, "Checkpoint save: " + ex.Message, LogType.Error);
            }
        }

        private static bool TryLoadCheckpoint()
        {
            if (!File.Exists(CheckpointPath) ||
                DateTime.Now - File.GetLastWriteTime(CheckpointPath) > TimeSpan.FromHours(5))
                return false;

            try
            {
                using var fs = File.OpenRead(CheckpointPath);
                var state = JsonSerializer.Deserialize<HistState>(fs);
                if (state is null) return false;

                foreach (var kv in state.CoinQueues)
                {
                    var parts = kv.Key.Split('|');
                    if (parts.Length != 3) continue;
                    coinData[(parts[0], parts[1], parts[2])] = new ConcurrentQueue<int>(kv.Value);
                }

                foreach (var kv in state.CoinHists)
                {
                    var parts = kv.Key.Split('|');
                    if (parts.Length != 3) continue;
                    coinHistograms[(parts[0], parts[1], parts[2])] = kv.Value;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Add(null, "Checkpoint load: " + ex.Message, LogType.Error);
                return false;
            }
        }

    }
}
