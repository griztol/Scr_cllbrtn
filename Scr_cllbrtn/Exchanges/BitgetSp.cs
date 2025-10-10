using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Scr_cllbrtn.Exchanges
{
    public class BitgetSp : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.bitget.com/api/v3/market/tickers?category=SPOT");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            JObject? j = JsonConvert.DeserializeObject<JObject>(ans);
            if (j?["data"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    string symbol = item["symbol"]!.ToString().Replace("_", "").ToUpperInvariant();
                    if (string.IsNullOrEmpty(item["ask1Price"]?.ToString()) || string.IsNullOrEmpty(item["bid1Price"]?.ToString()))
                        continue;
                    CurData curData = new(this, symbol)
                    {
                        askPrice = double.Parse(item["ask1Price"]!.ToString(), CultureInfo.InvariantCulture),
                        bidPrice = double.Parse(item["bid1Price"]!.ToString(), CultureInfo.InvariantCulture),
                        askAmount = double.Parse(item["ask1Size"]!.ToString(), CultureInfo.InvariantCulture),
                        bidAmount = double.Parse(item["bid1Size"]!.ToString(), CultureInfo.InvariantCulture)
                    };
                    res[symbol] = curData;
                }
            }
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            if (!meta[curNm].Active)
            {
                Logger.Add(curNm, "Not active in " + exName, LogType.Info);
                throw new Exception(curNm + "Not active in " + exName);
            }

            string ans = await SendApiRequestToExchangeAsync($"https://api.bitget.com/api/v3/market/orderbook?category=SPOT&symbol={curNm}&limit=10");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans)?["data"] as JObject;
            if (jsonData == null)
                throw new Exception("JSON parse error");

            var asksToken = jsonData["a"] as JArray;
            var bidsToken = jsonData["b"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0]!.Value<double>(), a[1]!.Value<double>() })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0]!.Value<double>(), b[1]!.Value<double>() })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, GlbConst.LiquidityCheckUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, GlbConst.LiquidityCheckUsd);

            CurData curData = new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = DateTime.UtcNow
            };

            return curData;
        }

        public override Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
            => Task.FromResult(new OrderResult("", ""));

        public override Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
            => Task.FromResult(new OrderResult("", ""));

        public override Task<OrderResult> CancelOrderAsync(string orderId, string curName)
            => Task.FromResult(new OrderResult("", ""));

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction) { }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction) { }

        public override void Unsubscribe(CurData crDt) { }

        public override Task<bool> UpdateBalanceAsync(CurData crDt)
            => Task.FromResult(false);

        public override Task<bool> CancelAllOrdersAsync(CurData crDt)
            => Task.FromResult(false);

        public override decimal GetBalance(CurData crDt)
            => 0m;

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.bitget.com/api/v3/market/instruments?category=SPOT");
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];
            if (data == null) return;
                foreach (var c in data)
                {
                    string curNm = c["symbol"]!.ToString().Replace("_", "").ToUpperInvariant();
                    decimal step = decimal.Parse(c["minOrderQty"]!.ToString(), CultureInfo.InvariantCulture);
                    bool active = c["status"]!.ToString().Equals("online", StringComparison.OrdinalIgnoreCase);

                    double minUsd = 5.0;
                    if (double.TryParse(c["minOrderAmount"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double mu))
                        minUsd = mu;

                    var m = new CoinMeta
                    {
                        Step = step,
                        Active = active,
                        InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                        FundingRate = 0,
                        LastUpdateTm = DateTime.UtcNow,
                        PricePrecision = c["pricePrecision"],
                        MinOrderUSDT = minUsd
                    };

                    base.meta.AddOrUpdate(curNm, m, (_, __) => m);
                }
        }

    }

}