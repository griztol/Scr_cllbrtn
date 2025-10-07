using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using CryptoExchange.Net.Authentication;
using Bitget.Net.Clients;
using Bitget.Net.Enums;
using Bitget.Net.Enums.V2;
using Bitget.Net.Objects.Models.V2;
using System.Collections.Concurrent;
using CryptoExchange.Net.Objects;

namespace Scr_cllbrtn.Exchanges
{
    public class BitgetFt : BaseExchange
    {
        BitgetRestClient httpClient;
        BitgetSocketClient socketClient;
        Dictionary<string, int> unsubscribeId = new();


        public BitgetFt()
        {
            httpClient = new BitgetRestClient(o =>
            {
                o.ApiCredentials = new ApiCredentials("bg_01ae9c5450d12ff0aa35ab8f5cbd3695", "83fe4d9c2139331ffddabb8504474e007dccfe410d08e93fda368d8c724f4a21",
                    "6212265c5be");
                o.OutputOriginalData = true;
            });

            socketClient = new BitgetSocketClient(o =>
            {
                o.ApiCredentials = new ApiCredentials("bg_01ae9c5450d12ff0aa35ab8f5cbd3695", "83fe4d9c2139331ffddabb8504474e007dccfe410d08e93fda368d8c724f4a21",
                    "6212265c5be");
                o.OutputOriginalData = true;
            });

            SubscribeToUpdateOrders();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.bitget.com/api/v3/market/tickers?category=USDT-FUTURES");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];
            if (data == null) return res;
            foreach (var item in data)
            {
                string name = item["symbol"].ToString();
                if (string.IsNullOrEmpty(item["ask1Price"].ToString()) || string.IsNullOrEmpty(item["bid1Price"].ToString()))
                    continue;

                CurData curData = new CurData(this, name)
                {
                    askPrice = double.Parse(item["ask1Price"].ToString(), CultureInfo.InvariantCulture),
                    bidPrice = double.Parse(item["bid1Price"].ToString(), CultureInfo.InvariantCulture),
                    askAmount = double.Parse(item["ask1Size"].ToString(), CultureInfo.InvariantCulture),
                    bidAmount = double.Parse(item["bid1Size"].ToString(), CultureInfo.InvariantCulture)
                };
                res[name] = curData;
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
                $"https://api.bitget.com/api/v3/market/orderbook?category=USDT-FUTURES&symbol={curNm}&limit=5");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);
            JObject item = JsonConvert.DeserializeObject<JObject>(ans)?["data"] as JObject;

            var asksToken = item?["a"] as JArray;
            var bidsToken = item?["b"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            long tsVal = 0;
            if (item?["ts"] != null)
                tsVal = long.Parse(item["ts"].ToString(), CultureInfo.InvariantCulture);
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds(tsVal).UtcDateTime;

            double multiplier = meta.TryGetValue(curNm, out var m) ? (double)m.Step : 1.0;

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0].Value<double>(), a[1].Value<double>() * multiplier })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0].Value<double>(), b[1].Value<double>() * multiplier })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, GlbConst.LiquidityCheckUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, GlbConst.LiquidityCheckUsd);

            return new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
            };
        }

        public override Task<OrderResult> BuyAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) =>
            PlaceOrderAsync(name, vol, price, OrderSide.Buy, noAlign, fok);

        public override Task<OrderResult> SellAsync(string name, decimal vol, decimal price, bool noAlign, bool fok) =>
            PlaceOrderAsync(name, vol, price, OrderSide.Sell, noAlign, fok);

        async Task<OrderResult> PlaceOrderAsync(string name, decimal vol, decimal price, OrderSide side, bool noAlign, bool fok)
        {
            vol = Math.Abs(vol);
            price = Math.Round(price, meta[name].PricePrecision);
            Logger.Add(name, $" Place {side} {exName} {vol} {price}", LogType.Info);

            var res = new OrderResult(exName, name);

            //decimal step = meta[name].Step;
            //decimal quantity = vol / step;

            OrderType type = price == 0 ? OrderType.Market : OrderType.Limit;
            TimeInForce? tif = price == 0 ? TimeInForce.ImmediateOrCancel : (fok ? TimeInForce.FillOrKill : TimeInForce.GoodTillCanceled);

            var rsp = await httpClient.FuturesApiV2.Trading.PlaceOrderAsync(
                BitgetProductTypeV2.UsdtFutures,
                name,
                "USDT",
                side,
                type,
                MarginMode.CrossMargin,
                vol,
                price == 0 ? null : price,
                tif,
                clientOrderId: noAlign ? $"{ tagNoAlign}{ DateTime.UtcNow.Ticks}" : null);

            if (!rsp.Success)
            {
                res.errMes = rsp.Error!.Message;
                Logger.Add(name, $"{exName} {rsp.Error!.Message}", LogType.Error);
                return res;
            }

            res.success = true;
            res.orderId = rsp.Data.OrderId;
            return res;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName)
        {
            OrderResult r = new OrderResult(exName, curName) { orderId = orderId };
            var rsp = await httpClient.FuturesApiV2.Trading.CancelOrderAsync(
                BitgetProductTypeV2.UsdtFutures,
                curName,
                orderId: orderId,
                marginAsset: "USDT");
            r.success = rsp.Success;
            if (!rsp.Success)
            {
                r.errMes = rsp.Error!.Message;
                Logger.Add(curName, $"{exName} {rsp.Error!.Message}", LogType.Error);
            }
            return r;
        }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction)
        {
            Task.Run(async () =>
            {
                var sub = await socketClient.FuturesApiV2.SubscribeToTickerUpdatesAsync(
                    BitgetProductTypeV2.UsdtFutures,
                    crDt.name,
                    update =>
                    {
                        var d = update.Data;
                        double step = meta.TryGetValue(crDt.name, out var m) ? (double)m.Step : 1.0;
                        crDt.askPrice = d.BestAskPrice.HasValue ? (double)d.BestAskPrice.Value : 0.0;
                        crDt.bidPrice = d.BestBidPrice.HasValue ? (double)d.BestBidPrice.Value : 0.0;
                        crDt.askAmount = d.BestAskQuantity.HasValue ? (double)(d.BestAskQuantity.Value * (decimal)step) : 0.0;
                        crDt.bidAmount = d.BestBidQuantity.HasValue ? (double)(d.BestBidQuantity.Value * (decimal)step) : 0.0;
                        Logger.Add(crDt.name, $" Deal {exName} prA: {crDt.askPrice}, prB: {crDt.bidPrice}, amA: {crDt.askAmount}, amB: {crDt.bidAmount};", LogType.Data);
                        updateAction(null);
                    });
                if (sub.Success)
                    unsubscribeId["TickerID_" + crDt.name] = sub.Data.Id;
            }).Wait();
        }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
        {
            wsSubscriptions[crDt.name + "order"] = filledAction;
        }

        void SubscribeToUpdateOrders()
        {
            Task.Run(async () =>
            {
                await socketClient.FuturesApiV2.SubscribeToUserTradeUpdatesAsync(BitgetProductTypeV2.UsdtFutures, update =>
                {
                    foreach (var t in update.Data)
                    {
                        string symbol = t.Symbol;
                        decimal step = meta.TryGetValue(symbol, out var m) ? m.Step : 1m;
                        decimal qty = t.Quantity * step;
                        decimal sign = t.Side == OrderSide.Buy ? 1m : -1m;
                        decimal signedQty = sign * qty;
                        bool noAlign = update.OriginalData != null && update.OriginalData.Contains(tagNoAlign, StringComparison.Ordinal);

                        string asset = symbol.Replace("USDT", "");
                        generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + signedQty;

                        var log = $"{exName} DEAL " +
                                  $"TradeId: {t.TradeId}  " +
                                  $"Side: {t.Side}  " +
                                  $"Qty: {qty}  " +
                                  $"Price: {t.Price}  ";

                        Logger.Add(symbol, log, LogType.Info);

                        if (!noAlign && wsSubscriptions.TryGetValue(symbol + "order", out var act))
                            act(signedQty);
                    }
                });
            }).Wait();
        }

        public override void Unsubscribe(CurData crDt)
        {
            if (unsubscribeId.TryGetValue("TickerID_" + crDt.name, out var id))
            {
                socketClient.UnsubscribeAsync(id).Wait();
                unsubscribeId.Remove("TickerID_" + crDt.name);
            }
            wsSubscriptions.Remove(crDt.name + "order");
        }

        public override async Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            var rsp = await httpClient.FuturesApiV2.Trading.CancelAllOrdersAsync(
                BitgetProductTypeV2.UsdtFutures,
                symbol: crDt.name,
                marginAsset: "USDT");

            if (rsp.Success || (rsp.Error?.Message?.Contains("No order to cancel") ?? false))
                return true;

            Logger.Add(crDt.name, "CancelAllOrders: " + rsp.Error!.Message, LogType.Error);
            return false;
        }

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            var rsp = await httpClient.FuturesApiV2.Trading.GetPositionsAsync(BitgetProductTypeV2.UsdtFutures, "USDT");
            if (!rsp.Success)
            {
                Logger.Add(crDt.name, exName + " UpdateBalance: " + rsp.Error!.Message, LogType.Error);
                return false;
            }

            foreach (var p in rsp.Data)
            {
                decimal step = meta.TryGetValue(p.Symbol, out var m) ? m.Step : 1m;
                decimal qty = p.Total * step;
                decimal sign = p.PositionSide == PositionSide.Long ? 1m : -1m;
                generalBalance.AddOrUpdate(p.Symbol.Replace("USDT", ""), sign * qty, (k, v) => sign * qty);
            }

            string assetName = crDt.name.Replace("USDT", "");
            if (!generalBalance.ContainsKey(assetName))
                generalBalance.TryAdd(assetName, 0);

            Logger.Add(crDt.name, "UpdateBalance " + exName + ": " + generalBalance[assetName], LogType.Info);
            return true;
        }

        public override decimal GetBalance(CurData crDt)
        {
            string asset = crDt.name.Replace("USDT", "");
            return generalBalance.TryGetValue(asset, out var bal) ? bal : 0m;
        }

        public override async Task RefreshMetadataAsync()
        {
            string tickersAns = await SendApiRequestToExchangeAsync("https://api.bitget.com/api/v3/market/tickers?category=USDT-FUTURES");
            var tickers = JsonConvert.DeserializeObject<dynamic>(tickersAns)?["data"];
            Dictionary<string, double> fundingRates = new(StringComparer.OrdinalIgnoreCase);

            if (tickers != null)
            {
                foreach (var ticker in tickers)
                {
                    string? symbol = ticker["symbol"]?.ToString();
                    if (string.IsNullOrEmpty(symbol))
                        continue;

                    string? fundingRateStr = ticker["fundingRate"]?.ToString();
                    if (!string.IsNullOrEmpty(fundingRateStr) && double.TryParse(fundingRateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double fundingRate))
                    {
                        fundingRates[symbol] = fundingRate;
                    }
                }
            }

            string ans = await SendApiRequestToExchangeAsync("https://api.bitget.com/api/v3/market/instruments?category=USDT-FUTURES");
            var data = JsonConvert.DeserializeObject<dynamic>(ans)?["data"];

            foreach (var item in data)
            {
                string curNm = item["symbol"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(curNm))
                    continue;

                decimal step = 1m;
                string? stepStr = item["quantityMultiplier"]?.ToString();
                if (!string.IsNullOrEmpty(stepStr) &&
                    decimal.TryParse(stepStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedStep) &&
                    parsedStep > 0)
                {
                    step = parsedStep;
                }

                bool active = item["status"] != null &&
                    item["status"].ToString().Equals("online", StringComparison.OrdinalIgnoreCase);

                byte pricePrecision = 0;
                string? pricePrecisionStr = item["pricePrecision"]?.ToString();
                if (!string.IsNullOrEmpty(pricePrecisionStr) &&
                    byte.TryParse(pricePrecisionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedPrecision))
                    pricePrecision = parsedPrecision;

                fundingRates.TryGetValue(curNm, out double fundingRateValue);

                var m = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var existing) && existing.InBlackList,
                    FundingRate = fundingRateValue * 100,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = pricePrecision,
                    MinOrderUSDT = 5
                };
                
                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }

    }
}