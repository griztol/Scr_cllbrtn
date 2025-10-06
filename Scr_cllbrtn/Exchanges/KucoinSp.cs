using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using Kucoin.Net.Enums;
using Kucoin.Net.Clients;
using Kucoin.Net.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Scr_cllbrtn.Exchanges
{
    public class KucoinSp : BaseExchange
    {
        KucoinRestClient httpClientJKorf;
        public KucoinSp() 
        {
            httpClientJKorf = new KucoinRestClient(options =>
            {

                //options.RequestTimeout = TimeSpan.FromSeconds(60);
                //options.FuturesOptions.ApiCredentials = new KucoinApiCredentials("65b75426bbd3b8000136e230", "66dda63b-37c0-4492-b154-3f9d319817e5", "Alisa123!!!");
                //options.SpotOptions.ApiCredentials = new KucoinApiCredentials("65b75426bbd3b8000136e230", "66dda63b-37c0-4492-b154-3f9d319817e5", "Alisa123!!!");
                options.SpotOptions.OutputOriginalData = false;
                //options.FuturesOptions.AutoTimestamp = false;
            });
        }
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.kucoin.com/api/v1/market/allTickers");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["data"]["ticker"])
            {
                CurData curData = new CurData(this, item["symbolName"].ToString().Replace("-", "").ToUpper());
                if (item["sell"].ToString() == "" || item["buy"].ToString() == "") { continue; }
                curData.askPrice = double.Parse(item["sell"].ToString());
                curData.bidPrice = double.Parse(item["buy"].ToString());
                curData.askAmount = double.Parse(item["bestAskSize"].ToString()); ;
                curData.bidAmount = double.Parse(item["bestBidSize"].ToString());
                res[curData.name] = curData;
            }
            //Logger.Add(exName + " " + res.Count);
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            if (!meta[curNm].Active)
            {
                Logger.Add(curNm, "Not active in " + exName, LogType.Info);
                throw new Exception(curNm + "Not active in " + exName);
            }

            string ans = await SendApiRequestToExchangeAsync(
                "https://api.kucoin.com/api/v1/market/orderbook/level2_20?symbol="
                + curNm.Replace("USDT", "-USDT"));

            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans)?["data"] as JObject;
            if (jsonData == null)
                throw new Exception("JSON parse error");

            double tsVal = jsonData["time"] != null ? jsonData["time"].Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)tsVal).UtcDateTime;

            var asksToken = jsonData["asks"] as JArray;
            var bidsToken = jsonData["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0].Value<double>(), a[1].Value<double>() })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0].Value<double>(), b[1].Value<double>() })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, (double)GlbConst.StepUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, (double)GlbConst.StepUsd);

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

        public override async Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) 
        {
            return await Task.FromResult(new OrderResult("", ""));
            var positionResultData = await httpClientJKorf.SpotApi.Trading.PlaceOrderAsync(
                            name + "-USDT",
                            OrderSide.Buy,
                            NewOrderType.Market,
                            quoteQuantity: vol);

            Logger.Add(name, "Result_Buy: " + positionResultData.Success, LogType.Result);
            if (!positionResultData.Success) { Logger.Add(name, "Error:" + positionResultData.Error.Message, LogType.Error); }
            return await Task.FromResult(new OrderResult("", ""));
        }

        public override async Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) 
        {
            return await Task.FromResult(new OrderResult("", ""));
            var positionResultData = await httpClientJKorf.SpotApi.Trading.PlaceOrderAsync(
                            name + "-USDT",
                            OrderSide.Sell,
                            NewOrderType.Market,
                            vol);

            Logger.Add(name, "Result_Sell: " + positionResultData.Success, LogType.Result);
            if (!positionResultData.Success) { Logger.Add(name, "Error:" + positionResultData.Error.Message, LogType.Error); }
            return await Task.FromResult(new OrderResult("", ""));
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName) { return await Task.FromResult(new OrderResult("", "")); }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction) { }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction) { }

        public override void Unsubscribe(CurData crDt) { }

        public override Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            throw new NotImplementedException();
        }


        public override decimal GetBalance(CurData crDt)
        {
            throw new NotImplementedException();
        }

        public override async Task RefreshMetadataAsync()
        {
            var info = JsonConvert.DeserializeObject<dynamic>(
                await SendApiRequestToExchangeAsync("https://api.kucoin.com/api/v1/symbols"));

            foreach (var c in info.data)
            {
                string curNm = c.symbol.ToString().Replace("-", "").ToUpper();

                decimal step = decimal.Parse(c.baseIncrement.ToString(), CultureInfo.InvariantCulture);
                bool active = c.enableTrading != null && (bool)c.enableTrading;

                var m = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = GetDecimalPlaces((double)c.priceIncrement)
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}
