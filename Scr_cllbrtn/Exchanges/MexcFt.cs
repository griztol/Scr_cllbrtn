using Mexc.Net.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Scr_cllbrtn.Exchanges
{
    internal class MexcFt : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://contract.mexc.com/api/v1/contract/ticker");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            var res = new Dictionary<string, CurData>(StringComparer.OrdinalIgnoreCase);

            var root = JsonConvert.DeserializeObject<JObject>(ans);
            if (root?["data"] is not JArray arr)
                return res;

            foreach (var item in arr)
            {
                var sym = item.Value<string>("symbol");
                if (string.IsNullOrEmpty(sym) || !sym.EndsWith("_USDT", StringComparison.OrdinalIgnoreCase))
                    continue;

                double? ask = item.Value<double?>("ask1");
                double? bid = item.Value<double?>("bid1");
                if (ask is null || bid is null)
                    continue;

                var curData = new CurData(this, sym.Replace("_", "").ToUpperInvariant())
                {
                    askPrice = ask.Value,
                    bidPrice = bid.Value,
                    askAmount = 0,
                    bidAmount = 0
                };

                res[curData.name] = curData;
            }

            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string ans = await SendApiRequestToExchangeAsync("https://contract.mexc.com/api/v1/contract/depth/" + curNm.Replace("USDT", "_USDT") + "?limit=1");

            var item = JsonConvert.DeserializeObject<dynamic>(ans)["data"];
            CurData curData = new CurData(this, curNm);
            //curData.balance = balance[curNm.ToString()];
            curData.askPrice = double.Parse(item["asks"][0][0].ToString(), CultureInfo.InvariantCulture);
            curData.bidPrice = double.Parse(item["bids"][0][0].ToString(), CultureInfo.InvariantCulture);

            double multiplier = meta.TryGetValue(curNm, out var m) ? (double)m.Step : 1.0;

            curData.askAmount = double.Parse(item["asks"][0][1].ToString(), CultureInfo.InvariantCulture) * multiplier;
            curData.bidAmount = double.Parse(item["bids"][0][1].ToString(), CultureInfo.InvariantCulture) * multiplier;

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
            string ans = await SendApiRequestToExchangeAsync("https://contract.mexc.com/api/v1/contract/detail");
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];
            if (data == null)
                return;

            foreach (var item in data)
            {
                string curNm = item["symbol"].ToString().Replace("_", "").ToUpper();

                decimal step = 1m;
                string? stepStr = item["contractSize"]?.ToString();
                if (!string.IsNullOrEmpty(stepStr))
                    decimal.TryParse(stepStr, NumberStyles.Any, CultureInfo.InvariantCulture, out step);

                bool active = item["contractStatus"] != null
                    ? item["contractStatus"].ToString() == "1"
                    : true;

                double fundingRate = 0;
                string? fundingRateStr = item["fundingRate"]?.ToString();
                if (!string.IsNullOrEmpty(fundingRateStr))
                    double.TryParse(fundingRateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out fundingRate);

                byte pricePrecision = 0;
                string? tickStr = item["priceTick"]?.ToString();
                if (!string.IsNullOrEmpty(tickStr) &&
                    double.TryParse(tickStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double tick))
                {
                    pricePrecision = GetDecimalPlaces(tick);
                }

                var metaEntry = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var existing) && existing.InBlackList,
                    FundingRate = fundingRate * 100,
                    PricePrecision = pricePrecision,
                    LastUpdateTm = DateTime.UtcNow
                };

                meta.AddOrUpdate(curNm, metaEntry, (_, __) => metaEntry);
            }
        }
    }
}
