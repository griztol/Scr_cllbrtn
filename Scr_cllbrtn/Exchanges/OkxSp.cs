using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Scr_cllbrtn.Exchanges
{
    public class OkxSp : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://www.okx.com/api/v5/market/tickers?instType=SPOT");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["data"])
            {
                CurData curData = new CurData(this, item["instId"].ToString().Replace("-", "").ToUpper());
                if (item["askPx"].ToString() == "" || item["bidPx"].ToString() == "")
                    continue;
                curData.askPrice = double.Parse(item["askPx"].ToString());
                curData.bidPrice = double.Parse(item["bidPx"].ToString());
                curData.askAmount = double.Parse(item["askSz"].ToString());
                curData.bidAmount = double.Parse(item["bidSz"].ToString());
                res[curData.name] = curData;
            }
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string instId = curNm.Replace("USDT", "-USDT");
            string ans = await SendApiRequestToExchangeAsync($"https://www.okx.com/api/v5/market/books?instId={instId}&sz=5");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject item = JsonConvert.DeserializeObject<JObject>(ans)?["data"]?[0] as JObject
                ?? throw new Exception("Invalid response");

            double tsVal = item["ts"] != null ? double.Parse(item["ts"].ToString(), CultureInfo.InvariantCulture) : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)tsVal).UtcDateTime;

            var asksToken = item["asks"] as JArray;
            var bidsToken = item["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            double multiplier = meta.TryGetValue(curNm, out var m) ? (double)m.Step : 1.0;

            List<double[]> asks = asksToken
                .Select(a => new double[]
                {
                    double.Parse(a[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(a[1]!.ToString(), CultureInfo.InvariantCulture) * multiplier
                })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[]
                {
                    double.Parse(b[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(b[1]!.ToString(), CultureInfo.InvariantCulture) * multiplier
                })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, 5);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, 5);

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

        public override Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) => Task.FromResult(new OrderResult("", ""));
        public override Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) => Task.FromResult(new OrderResult("", ""));
        public override Task<OrderResult> CancelOrderAsync(string orderId, string curName) => Task.FromResult(new OrderResult("", ""));
        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction) { }
        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction) { }
        public override void Unsubscribe(CurData crDt) { }
        public override Task<bool> CancelAllOrdersAsync(CurData crDt) => Task.FromResult(false);
        public override decimal GetBalance(CurData crDt) => 0m;
        public override Task<bool> UpdateBalanceAsync(CurData crDt) => Task.FromResult(false);

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://www.okx.com/api/v5/public/instruments?instType=SPOT");

            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["data"])
            {
                string curNm = item["instId"].ToString().Replace("-", "").ToUpper();

                decimal step = 0m;
                if (item["lotSz"] != null)
                    step = decimal.Parse(item["lotSz"].ToString(), CultureInfo.InvariantCulture);
                else if (item["minSz"] != null)
                    step = decimal.Parse(item["minSz"].ToString(), CultureInfo.InvariantCulture);

                bool active = ((string)item["state"]).Equals("live", StringComparison.OrdinalIgnoreCase);

                var m = new CoinMeta {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = GetDecimalPlaces((double)item["tickSz"]),
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}
