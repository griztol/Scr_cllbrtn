using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Scr_cllbrtn.Exchanges
{
    public class KucoinFt : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api-futures.kucoin.com/api/v1/allTickers");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            var data = JsonConvert.DeserializeObject<dynamic>(ans)["data"];
            foreach (var item in data)
            {
                string name = item["symbol"].ToString().ToUpper();
                if (string.IsNullOrEmpty(item["bestAskPrice"].ToString()) || string.IsNullOrEmpty(item["bestBidPrice"].ToString()))
                    continue;

                CurData curData = new CurData(this, name)
                {
                    askPrice = double.Parse(item["bestAskPrice"].ToString()),
                    bidPrice = double.Parse(item["bestBidPrice"].ToString()),
                    askAmount = double.Parse(item["bestAskSize"].ToString()),
                    bidAmount = double.Parse(item["bestBidSize"].ToString())
                };
                res[name] = curData;
            }
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string ans = await SendApiRequestToExchangeAsync(
                $"https://api-futures.kucoin.com/api/v1/level2/depth20?symbol={curNm.Replace("USDT", "USDTM")}");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            var item = JsonConvert.DeserializeObject<dynamic>(ans)["data"];

            double multiplier = (double)meta[curNm].Step;

            List<double[]> asks = new List<double[]>();
            foreach (var a in item["asks"])
            {
                asks.Add(new double[]
                {
                    (double)a[0],
                    (double)a[1] * multiplier
                });
            }

            List<double[]> bids = new List<double[]>();
            foreach (var b in item["bids"])
            {
                bids.Add(new double[]
                {
                    (double)b[0],
                    (double)b[1] * multiplier
                });
            }

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, 5);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, 5);

            DateTime ts = DateTime.UtcNow;
            if (item["ts"] != null)
            {
                ts = DateTimeOffset
                    .FromUnixTimeMilliseconds(((long)item["ts"]) / 1_000_000)
                    .UtcDateTime;
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

        public override async Task RefreshMetadataAsync()
        {
            var info = JsonConvert.DeserializeObject<dynamic>(await SendApiRequestToExchangeAsync("https://api-futures.kucoin.com/api/v1/contracts/active"));

            foreach (var c in info["data"])
            {
                string curNm = c["symbol"].ToString().Replace("USDTM", "USDT"); ;
                var m = new CoinMeta {
                    Step = (decimal)c["multiplier"],
                    Active = ((string)c["status"]).Equals("Open", StringComparison.OrdinalIgnoreCase),
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = c["fundingFeeRate"] != null ? (double)c["fundingFeeRate"] * 100 : 0,
                    LastUpdateTm = DateTime.UtcNow
                };
                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }

        public override Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) => Task.FromResult(new OrderResult("", ""));
        public override Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) => Task.FromResult(new OrderResult("", ""));
        public override Task<OrderResult> CancelOrderAsync(string orderId, string curName) => Task.FromResult(new OrderResult("", ""));
        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction) { }
        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction) { }
        public override void Unsubscribe(CurData crDt) { }
        public override Task<bool> CancelAllOrdersAsync(CurData crDt) => Task.FromResult(false);
        public override Task<bool> UpdateBalanceAsync(CurData crDt) => Task.FromResult(false);
        public override decimal GetBalance(CurData crDt) => 0m;
    }
}
