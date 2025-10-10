using Microsoft.VisualBasic;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Scr_cllbrtn
{
    public record CoinMeta
    {
        public decimal Step { get; init; }
        public bool Active { get; init; }
        public bool InBlackList { get; init; }
        public DateTime LastUpdateTm { get; init; }
        public double FundingRate { get; init; }
        public byte PricePrecision { get; init; }
        public double MinOrderUSDT { get; init; }
        public Boolean withdrawDisabled { get; init; }
    };

    public abstract class BaseExchange
    {
        public virtual double TotalUsdt { get; }
        protected string tagNoAlign = "noAlign";
        public readonly ConcurrentDictionary<string, CoinMeta> meta = new(StringComparer.OrdinalIgnoreCase);
        protected Dictionary<string, Action<object>> wsSubscriptions = new Dictionary<string, Action<object>>();

        protected ConcurrentDictionary<string, decimal> generalBalance { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string exName;

        public BaseExchange()
        {
            exName = ToString()!.Substring(ToString()!.LastIndexOf(".") + 1);
        }

        private HttpClient httpClientNoKey = new() { Timeout = TimeSpan.FromMilliseconds(15000) };

        public abstract Task RefreshMetadataAsync();
        public abstract Task<Dictionary<string, CurData>> GetAllCurrenciesAsync();
        protected abstract Dictionary<string, CurData> AnswerToDictionary(string ans);
        public abstract Task<CurData> GetLastPriceAsync(string curNm);
        public abstract void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction);
        public abstract void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction);
        public abstract void Unsubscribe(CurData crDt);
        public abstract Task<bool> CancelAllOrdersAsync(CurData crDt);
        public abstract Task<bool> UpdateBalanceAsync(CurData crDt);
        public abstract decimal GetBalance(CurData crDt);

        protected async Task<string> SendApiRequestToExchangeAsync(string reqMes)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, reqMes);
            HttpResponseMessage response = await httpClientNoKey.SendAsync(request);

            string ans = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
            return ans;
        }

        public abstract Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok);
        public abstract Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok);
        public abstract Task<OrderResult> CancelOrderAsync(string orderId, string curName);

        /// <summary>
        /// Calculates an effective price taking the first level completely and
        /// then filling the remaining required dollars from subsequent levels.
        /// </summary>
        /// <param name="levels">List of [price, quantity] arrays.</param>
        /// <param name="needDollars">How many dollars worth of volume is required.</param>
        /// <returns>Tuple of average price and total quantity.</returns>
        protected (double price, double amount) CalculatePriceWithFirstLevelAlwaysTaken(List<double[]> levels, double needDollars)
        {
            if (levels == null || levels.Count == 0)
                return (0.0, 0.0);

            double costSoFar = 0.0;
            double amountSoFar = 0.0;

            double firstPrice = levels[0][0];
            double firstAmount = levels[0][1];

            costSoFar = firstPrice * firstAmount;
            amountSoFar = firstAmount;

            if (costSoFar <= needDollars)
            {
                for (int i = 1; i < levels.Count; i++)
                {
                    double price = levels[i][0];
                    double amount = levels[i][1];

                    double lvlCost = price * amount;

                    if (costSoFar + lvlCost > needDollars)
                    {
                        double neededDollars = needDollars - costSoFar;
                        double partialAmount = neededDollars / price;
                        costSoFar += neededDollars;
                        amountSoFar += partialAmount;
                        break;
                    }
                    else
                    {
                        costSoFar += lvlCost;
                        amountSoFar += amount;
                    }
                }
            }

            double avgPrice = amountSoFar == 0.0 ? 0.0 : costSoFar / amountSoFar;
            return (avgPrice, amountSoFar);
        }

        public void ApplyBlacklistToMeta(IEnumerable<string> coins)
        {
            foreach (var coin in coins)
            {
                if (string.IsNullOrWhiteSpace(coin)) continue;
                if (meta.TryGetValue(coin, out var m))
                    meta[coin] = m with { InBlackList = true };
            }
        }

        protected byte GetDecimalPlaces(double tick)
        {
            // Проверка на некорректные значения (ноль, отрицательные числа, или 1 и больше)
            if (tick <= 0 || tick >= 1) {  return 9; }
            double log = Math.Log10(tick);
            return (byte)Math.Round(-log);
        }


    }
}
