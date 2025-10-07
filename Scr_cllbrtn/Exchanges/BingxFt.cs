using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Scr_cllbrtn.Exchanges
{
    public class BingxFt : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://open-api.bingx.com/openApi/swap/v2/quote/ticker");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];
            if (data == null)
                return res;

            foreach (var item in data)
            {
                string curNm = ((string)item["symbol"]).Replace("-", "").ToUpper();
                string? askStr = item["askPrice"]?.ToString();
                string? bidStr = item["bidPrice"]?.ToString();
                string? askAmtStr = item["askQty"]?.ToString();
                string? bidAmtStr = item["bidQty"]?.ToString();

                if (string.IsNullOrEmpty(askStr) || string.IsNullOrEmpty(bidStr))
                    continue;

                CurData curData = new CurData(this, curNm)
                {
                    askPrice = double.Parse(askStr, CultureInfo.InvariantCulture),
                    bidPrice = double.Parse(bidStr, CultureInfo.InvariantCulture),
                    askAmount = !string.IsNullOrEmpty(askAmtStr) ? double.Parse(askAmtStr, CultureInfo.InvariantCulture) : 0.0,
                    bidAmount = !string.IsNullOrEmpty(bidAmtStr) ? double.Parse(bidAmtStr, CultureInfo.InvariantCulture) : 0.0
                };
                res[curNm] = curData;
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

            string symbol = curNm.Replace("USDT", "-USDT");
            string ans = await SendApiRequestToExchangeAsync($"https://open-api.bingx.com/openApi/swap/v2/quote/depth?symbol={symbol}&limit=5");

            JObject item = JsonConvert.DeserializeObject<JObject>(ans)?["data"] as JObject ?? new JObject();

            var asksToken = item["asksCoin"] as JArray ?? new JArray();
            var bidsToken = item["bidsCoin"] as JArray ?? new JArray();

            List<double[]> asks = asksToken
                .Select(a => new double[]
                {
                    double.Parse(a[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(a[1]!.ToString(), CultureInfo.InvariantCulture)
                })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[]
                {
                    double.Parse(b[0]!.ToString(), CultureInfo.InvariantCulture),
                    double.Parse(b[1]!.ToString(), CultureInfo.InvariantCulture)
                })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, GlbConst.LiquidityCheckUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, GlbConst.LiquidityCheckUsd);

            DateTime ts = DateTime.UtcNow;
            if (item["T"] != null)
            {
                ts = DateTimeOffset.FromUnixTimeMilliseconds(item["T"]!.Value<long>()).UtcDateTime;
            }

            return new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
            };
        }

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://open-api.bingx.com/openApi/swap/v2/quote/contracts");
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];

            Dictionary<string, double> fundingRates = new(StringComparer.OrdinalIgnoreCase);
            string premiumAns = await SendApiRequestToExchangeAsync("https://open-api.bingx.com/openApi/swap/v2/quote/premiumIndex");
            var premiumData = JsonConvert.DeserializeObject<dynamic>(premiumAns)?["data"];
            if (premiumData != null)
            {
                foreach (var premiumItem in premiumData)
                {
                    string premiumSymbol = ((string)premiumItem["symbol"]).Replace("-", "").ToUpper();
                    string? premiumFundingStr = premiumItem["lastFundingRate"]?.ToString();
                    if (!string.IsNullOrEmpty(premiumFundingStr) &&
                        double.TryParse(premiumFundingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double premiumFunding))
                    {
                        fundingRates[premiumSymbol] = premiumFunding;
                    }
                }
            }

            foreach (var item in data)
            {
                string curNm = ((string)item["symbol"]).Replace("-", "").ToUpper();

                decimal step = 1m;
                string? stepStr = item["contractSize"]?.ToString() ?? item["lotSize"]?.ToString();
                if (!string.IsNullOrEmpty(stepStr))
                    decimal.TryParse(stepStr, NumberStyles.Any, CultureInfo.InvariantCulture, out step);

                bool active = (item["status"]?.ToString() ?? "1") == "1";

                double fundingRate = 0;
                if (!fundingRates.TryGetValue(curNm, out fundingRate))
                {
                    string? frStr = item["fundingRate"]?.ToString();
                    if (!string.IsNullOrEmpty(frStr))
                        double.TryParse(frStr, NumberStyles.Any, CultureInfo.InvariantCulture, out fundingRate);
                }

                var m = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) && b.InBlackList,
                    FundingRate = fundingRate * 100,
                    LastUpdateTm = DateTime.UtcNow
                };

                meta.AddOrUpdate(curNm, m, (_, __) => m);
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