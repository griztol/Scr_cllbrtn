using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Screener.Exchanges
{
    public class BingxSp : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://open-api.bingx.com/openApi/spot/v1/ticker/bookTicker");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            var dataToken = JsonConvert.DeserializeObject<JObject>(ans)?["data"] as JArray;
            if (dataToken == null)
                return res;

            foreach (var item in dataToken)
            {
                string curNm = item["symbol"]!.ToString().Replace("-", "").ToUpperInvariant();
                var cd = new CurData(this, curNm);
                if (string.IsNullOrEmpty(item["askPrice"]?.ToString()) || string.IsNullOrEmpty(item["bidPrice"]?.ToString()))
                    continue;
                cd.askPrice = double.Parse(item["askPrice"]!.ToString(), CultureInfo.InvariantCulture);
                cd.bidPrice = double.Parse(item["bidPrice"]!.ToString(), CultureInfo.InvariantCulture);
                cd.askAmount = double.Parse(item["askVolume"]!.ToString(), CultureInfo.InvariantCulture);
                cd.bidAmount = double.Parse(item["bidVolume"]!.ToString(), CultureInfo.InvariantCulture);
                res[curNm] = cd;
            }
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string symbol = curNm.Replace("USDT", "-USDT");
            string ans = await SendApiRequestToExchangeAsync($"https://open-api.bingx.com/openApi/spot/v1/market/depth?symbol={symbol}&limit=5");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject obj = JsonConvert.DeserializeObject<JObject>(ans) ?? new JObject();
            JObject data = obj["data"] as JObject ?? throw new Exception("Invalid response");

            var asksToken = data["asks"] as JArray;
            var bidsToken = data["bids"] as JArray;

            List<double[]> asks = asksToken != null
                ? asksToken.Select(a => new double[]
                {
                    double.Parse(a[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(a[1]!.ToString(), CultureInfo.InvariantCulture)
                }).ToList()
                : new List<double[]>();

            List<double[]> bids = bidsToken != null
                ? bidsToken.Select(b => new double[]
                {
                    double.Parse(b[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(b[1]!.ToString(), CultureInfo.InvariantCulture)
                }).ToList()
                : new List<double[]>();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, 5);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, 5);

            DateTime ts = DateTime.UtcNow;
            if (obj["timestamp"] != null)
            {
                ts = DateTimeOffset.FromUnixTimeMilliseconds(obj["timestamp"].Value<long>()).UtcDateTime;
            }
            else if (data["ts"] != null)
            {
                ts = DateTimeOffset.FromUnixTimeMilliseconds(data["ts"].Value<long>()).UtcDateTime;
            }

            CurData curData = new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
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
        public override Task<bool> CancelAllOrdersAsync(CurData crDt) => Task.FromResult(false);
        public override decimal GetBalance(CurData crDt) => 0m;
        public override Task<bool> UpdateBalanceAsync(CurData crDt) => Task.FromResult(false);

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://open-api.bingx.com/openApi/spot/v1/common/symbols");
            var dataToken = JsonConvert.DeserializeObject<JObject>(ans)?["data"]?["symbols"] as JArray;
            if (dataToken == null)
                return;

            foreach (var item in dataToken)
            {
                string curNm = item["symbol"]!.ToString().Replace("-", "").ToUpperInvariant();

                decimal step = 0m;
                if (item["stepSize"] != null)
                    step = decimal.Parse(item["stepSize"]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

                bool active = item["status"]?.Value<int>() == 1
                    && item["apiStateSell"]?.Value<bool>() == true
                    && item["apiStateBuy"]?.Value<bool>() == true;

                double minBuy = 0;
                if (item["minNotional"] != null)
                    minBuy = double.Parse(item["minNotional"]!.ToString(), CultureInfo.InvariantCulture);

                var m = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = GetDecimalPlaces((double)item["tickSize"]),
                    MinOrderUSDT = minBuy
                };

                meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}