using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screener.Exchanges
{
    public class PoloniexSp : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.poloniex.com/markets/ticker24h");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans))
            {
                CurData curData = new CurData(this, item["symbol"].ToString().Replace("_", "").ToUpper());
                if (item["ask"].ToString() == "" || item["bid"].ToString() == "") { continue; }
                curData.askPrice = double.Parse(item["ask"].ToString());
                curData.bidPrice = double.Parse(item["bid"].ToString());
                curData.askAmount = double.Parse(item["askQuantity"].ToString());
                curData.bidAmount = double.Parse(item["bidQuantity"].ToString());
                if (curData.askAmount == 0 || curData.bidAmount == 0) { continue; }
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
                "https://api.poloniex.com/markets/" + curNm.Replace("USDT", "_USDT") + "/orderBook?limit=5");

            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans);
            if (jsonData == null)
                throw new Exception("JSON parse error");

            double tsVal = jsonData["ts"] != null ? jsonData["ts"]!.Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)tsVal).LocalDateTime;

            var asksToken = jsonData["asks"] as JArray;
            var bidsToken = jsonData["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            var asks = jsonData["asks"]!          // JArray
                        .Values<double>()         // → IEnumerable<double>
                        .Chunk(2)                 // разбиваем по 2
                        .Select(p => new[] { p[0], p[1] })
                        .ToList();

            var bids = jsonData["bids"]!
                        .Values<double>()
                        .Chunk(2)
                        .Select(p => new[] { p[0], p[1] })
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

        public override async Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) { return await Task.FromResult(new OrderResult("", "")); }

        public override async Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) { return await Task.FromResult(new OrderResult("", "")); }

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
                await SendApiRequestToExchangeAsync("https://api.poloniex.com/markets"));

            foreach (var c in info)
            {
                string curNm = ((string)c.symbol).Replace("_", "").ToUpper();

                decimal step = 0.00000001m;
                if (c.quantityScale != null)
                {
                    step = (decimal)Math.Pow(10, -(int)c.quantityScale);
                }
                else if (c.minQuantity != null)
                {
                    step = decimal.Parse((string)c.minQuantity, CultureInfo.InvariantCulture);
                }

                bool active = true;
                if (c.state != null)
                    active = ((string)c.state).Equals("NORMAL", StringComparison.OrdinalIgnoreCase);
                if (c.visible != null)
                    active &= (bool)c.visible;

                byte precision = 9;
                if (c.priceScale != null)
                    precision = (byte)c.priceScale;

                var m = new CoinMeta {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = precision
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}
