using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Scr_cllbrtn.Exchanges
{
    public class GateSp : BaseExchange
    {
        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.gateio.ws/api/v4/spot/tickers");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            JArray? arr = JsonConvert.DeserializeObject<JArray>(ans);
            if (arr == null)
                return res;

            foreach (var item in arr)
            {
                string? symbol = item["currency_pair"]?.ToString();
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                symbol = symbol.Replace("_", string.Empty).ToUpperInvariant();

                string? askStr = item["lowest_ask"]?.ToString();
                string? bidStr = item["highest_bid"]?.ToString();
                if (string.IsNullOrWhiteSpace(askStr) || string.IsNullOrWhiteSpace(bidStr))
                    continue;

                if (!double.TryParse(askStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double askPrice))
                    continue;
                if (!double.TryParse(bidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double bidPrice))
                    continue;

                double askAmount = 0;
                double bidAmount = 0;
                double.TryParse(item["base_volume"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out askAmount);
                double.TryParse(item["quote_volume"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out bidAmount);

                CurData curData = new CurData(this, symbol)
                {
                    askPrice = askPrice,
                    bidPrice = bidPrice,
                    askAmount = askAmount,
                    bidAmount = bidAmount,
                    Timestamp = DateTime.UtcNow
                };

                res[symbol] = curData;
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

            string ans = await SendApiRequestToExchangeAsync(
                "https://api.gateio.ws/api/v4/spot/order_book?limit=10&currency_pair="
                + curNm.Replace("USDT", "_USDT")
            );
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans);
            if (jsonData == null)
                throw new Exception("JSON parse error");

            double tsVal = jsonData["current"] != null ? jsonData["current"].Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)(tsVal)).LocalDateTime;

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

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, GlbConst.LiquidityCheckUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, GlbConst.LiquidityCheckUsd);

            var curData = new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
            };

            return curData;
        }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction)
            => throw new NotImplementedException();

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
            => throw new NotImplementedException();

        public override void Unsubscribe(CurData crDt)
            => throw new NotImplementedException();

        public override Task<bool> CancelAllOrdersAsync(CurData crDt)
            => throw new NotImplementedException();

        public override Task<bool> UpdateBalanceAsync(CurData crDt)
            => throw new NotImplementedException();

        public override decimal GetBalance(CurData crDt)
            => throw new NotImplementedException();

        public override Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
            => throw new NotImplementedException();

        public override Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok)
            => throw new NotImplementedException();

        public override Task<OrderResult> CancelOrderAsync(string orderId, string curName)
            => throw new NotImplementedException();

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.gateio.ws/api/v4/spot/currency_pairs");
            JArray? arr = JsonConvert.DeserializeObject<JArray>(ans);
            if (arr == null)
                return;

            foreach (var item in arr)
            {
                string? symbol = item["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                symbol = symbol.Replace("_", string.Empty).ToUpperInvariant();

                decimal step = 0m;
                decimal.TryParse(item["min_base_amount"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out step);

                bool active = string.Equals(item["trade_status"]?.ToString(), "tradable", StringComparison.OrdinalIgnoreCase);

                double minQuote = 0;
                double.TryParse(item["min_quote_amount"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out minQuote);

                byte pricePrecision = 9;
                if (double.TryParse(item["order_price_round"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double priceTick) && priceTick > 0)
                {
                    pricePrecision = GetDecimalPlaces(priceTick);
                }
                else if (byte.TryParse(item["precision"]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out byte precision))
                {
                    pricePrecision = precision;
                }

                var existing = meta.TryGetValue(symbol, out var m) ? m : null;

                var coinMeta = new CoinMeta
                {
                    Step = step > 0 ? step : existing?.Step ?? 0m,
                    Active = active,
                    InBlackList = existing?.InBlackList ?? false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = pricePrecision,
                    MinOrderUSDT = minQuote
                };

                meta.AddOrUpdate(symbol, coinMeta, (_, __) => coinMeta);
            }
        }
    }
}
