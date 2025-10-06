using Bybit.Net.Enums;
using Bybit.Net.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CryptoExchange.Net.Authentication;

namespace Screener.Exchanges
{
    public class BybitFt : BaseExchange
    {
        BybitRestClient httpClientJKorf;
        public BybitFt()
        {
            httpClientJKorf = new BybitRestClient(options =>
            {
                //options.RequestTimeout = TimeSpan.FromSeconds(60);
                options.ApiCredentials = new ApiCredentials("k6eFdQUt8KUqdMLuKp", "3yHeWYjEeMXKt3G909SgR2G9pueLHvzdbNqO");
                options.OutputOriginalData = true;
                //options.FuturesOptions.AutoTimestamp = false;
            });
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.bybit.com/v5/market/tickers?category=linear");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["result"]["list"])
            {
                CurData curData = new CurData(this, item["symbol"].ToString().ToUpper());
                if (item["ask1Price"].ToString() == "" || item["bid1Price"].ToString() == "") { continue; }
                curData.askPrice = double.Parse(item["ask1Price"].ToString());
                curData.bidPrice = double.Parse(item["bid1Price"].ToString());
                curData.askAmount = double.Parse(item["ask1Size"].ToString());
                curData.bidAmount = double.Parse(item["bid1Size"].ToString());
                res[curData.name] = curData;
            }
            //Logger.Add(exName + " " + res.Count);
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string ans = await SendApiRequestToExchangeAsync(
                $"https://api.bybit.com/v5/market/orderbook?category=linear&symbol={curNm}&limit=5");

            JObject? item = JsonConvert.DeserializeObject<JObject>(ans)?["result"] as JObject;

            var asksToken = item?["a"] as JArray;
            var bidsToken = item?["b"] as JArray;

            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            double multiplier = meta.TryGetValue(curNm, out var m) ? (double)m.Step : 1.0;

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0].Value<double>(), a[1].Value<double>() * multiplier })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0].Value<double>(), b[1].Value<double>() * multiplier })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, 5);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, 5);

            DateTime ts = DateTime.UtcNow;
            if (item?["ts"] != null)
            {
                ts = DateTimeOffset.FromUnixTimeMilliseconds(item["ts"].Value<long>()).UtcDateTime;
            }

            CurData curData = new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
            };

            Logger.Add(curNm, exName + " bid: " + curData.bidPrice + " adk: " + curData.askPrice, LogType.Data);
            return curData;
        }

        public override async Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
        {
            var positionResultData = await httpClientJKorf.V5Api.Trading.PlaceOrderAsync(
                Bybit.Net.Enums.Category.Linear,
                name,
                Bybit.Net.Enums.OrderSide.Buy,
                Bybit.Net.Enums.NewOrderType.Market,
                vol);

            Logger.Add(name, "Result_Buy: " + positionResultData.Success, LogType.Result);
            if (!positionResultData.Success) { Logger.Add(name, positionResultData.Error.Message, LogType.Error); }
            //return await Task.FromResult(positionResultData.Success.ToString());
            return await Task.FromResult(new OrderResult("", ""));
        }

        public override async Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
        {
            var positionResultData = await httpClientJKorf.V5Api.Trading.PlaceOrderAsync(
                Bybit.Net.Enums.Category.Linear,
                name,
                Bybit.Net.Enums.OrderSide.Sell,
                Bybit.Net.Enums.NewOrderType.Market,
                vol);

            Logger.Add(name, "Result_Sell: " + positionResultData.Success, LogType.Result);
            if (!positionResultData.Success) { Logger.Add(name, positionResultData.Error.Message, LogType.Error); }
            //return await Task.FromResult(positionResultData.Success.ToString());
            return await Task.FromResult(new OrderResult("", ""));
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName) { return await Task.FromResult(new OrderResult("", "")); }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction) { }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction) { }
        public override void Unsubscribe(CurData crDt) { }

        public override Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            throw new NotImplementedException();
        }

        public override decimal GetBalance(CurData crDt)
        {
            throw new NotImplementedException();
        }

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync(
                "https://api.bybit.com/v5/market/instruments-info?category=linear");

            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["result"]["list"])
            {
                string curNm = item["symbol"].ToString().ToUpper();

                decimal step = decimal.Parse(
                    item["lotSizeFilter"]["qtyStep"].ToString(),
                    CultureInfo.InvariantCulture);

                bool active = item["status"] != null &&
                    ((string)item["status"]).Equals("Trading", StringComparison.OrdinalIgnoreCase);

                var m = new CoinMeta {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}
