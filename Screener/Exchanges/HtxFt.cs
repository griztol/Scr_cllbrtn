using CryptoExchange.Net.Authentication;
using HTX.Net;
using HTX.Net.Clients;
using HTX.Net.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Threading.Tasks;

namespace Screener.Exchanges
{
    public class HtxFt : BaseExchange
    {
        private sealed record CoinMetaLocal : CoinMeta
        {
            public int Test { get; init; }
        }

        HTXRestClient httpClient;
        HTXSocketClient socketClient;
        Dictionary<string, int> unsubscribeId = new();
        public HtxFt()
        {
            httpClient = new HTXRestClient(o =>
            {
                o.ApiCredentials = new ApiCredentials("be7759e0-b1ca883f-3d2xc4v5bu-c5d54", "a8086e8f-b946ab4e-d548e16c-8017a");
                o.UsdtMarginSwapOptions.OutputOriginalData = true;
            });

            socketClient = new HTXSocketClient(o =>
            {
                o.ApiCredentials = new ApiCredentials("be7759e0-b1ca883f-3d2xc4v5bu-c5d54", "a8086e8f-b946ab4e-d548e16c-8017a");
                o.UsdtMarginSwapOptions.OutputOriginalData = true;
            });

            SubscribeToUpdateOrders();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.hbdm.com/linear-swap-ex/market/detail/batch_merged");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            var data = JsonConvert.DeserializeObject<dynamic>(ans)["ticks"];
            foreach (var item in data)
            {
                string contract = item["contract_code"].ToString();
                string curNm = contract.Replace("-", "");

                var askArr = item["ask"] as JArray;
                var bidArr = item["bid"] as JArray;
                if (askArr == null || bidArr == null || askArr.Count < 2 || bidArr.Count < 2)
                    continue;

                CurData curData = new CurData(this, curNm)
                {
                    askPrice = askArr[0]!.Value<double>(),
                    askAmount = askArr[1]!.Value<double>(),
                    bidPrice = bidArr[0]!.Value<double>(),
                    bidAmount = bidArr[1]!.Value<double>()
                };
                res[curNm] = curData;
            }
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string code = curNm.Replace("USDT", "-USDT");
            string ans = await SendApiRequestToExchangeAsync($"https://api.hbdm.com/linear-swap-ex/market/depth?contract_code={code}&size=5&type=step6");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject obj = JsonConvert.DeserializeObject<JObject>(ans) ?? new JObject();

            double tsVal = obj["ts"] != null ? obj["ts"].Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)tsVal).UtcDateTime;

            var item = obj["tick"];
            var asksToken = item?["asks"] as JArray;
            var bidsToken = item?["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            double multiplier = meta.TryGetValue(curNm, out var m) ? (double)m.Step : 1.0;

            List<double[]> asks = asksToken.Select(a => new double[]
            {
                a[0]!.Value<double>(),
                a[1]!.Value<double>() * multiplier
            }).ToList();

            List<double[]> bids = bidsToken.Select(b => new double[]
            {
                b[0]!.Value<double>(),
                b[1]!.Value<double>() * multiplier
            }).ToList();

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

        public override Task<OrderResult> BuyAsync(string c, decimal v, decimal p, bool noAlign, bool fok) =>
            PlaceOrderAsync(OrderSide.Buy, c, v, p, noAlign, fok);

        public override Task<OrderResult> SellAsync(string c, decimal v, decimal p, bool noAlign, bool fok) =>
            PlaceOrderAsync(OrderSide.Sell, c, v, p, noAlign, fok);

        private async Task<OrderResult> PlaceOrderAsync(OrderSide dir, string curName, decimal vol, decimal price, bool noAlign, bool fok)
        {
            vol = Math.Abs(vol);
            price = Math.Round(price, meta[curName].PricePrecision);
            Logger.Add(curName, $" Place {dir} {exName} {vol} {price}", LogType.Info);

            var orderResult = new OrderResult(exName, curName);
            string contract = curName.Replace("USDT", "-USDT");

            decimal step = meta[curName].Step;
            long size = (long)(Math.Abs(vol) / step);

            var rsp = await httpClient.UsdtFuturesApi.Trading.PlaceCrossMarginOrderAsync(
                contractCode: contract,
                quantity: size,
                side: dir,
                leverageRate: 5,
                orderPriceType: price == 0 ? OrderPriceType.Optimal5 : OrderPriceType.Limit,
                price: price == 0 ? null : price,
                //offset: dir == OrderSide.Sell ? Offset.Open : Offset.Close,
                clientOrderId: noAlign ? DateTime.UtcNow.Ticks : null);

            if (!rsp.Success)
            {
                orderResult.errMes = rsp.Error!.Message;
                Logger.Add(curName, $"{exName} {rsp.Error.Message}", LogType.Error);
                return orderResult;
            }

            orderResult.success = true;
            orderResult.orderId = rsp.Data.OrderId.ToString();
            return orderResult;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName)
        {
            OrderResult r = new OrderResult(exName, curName) { orderId = orderId };
            string contract = curName.Replace("USDT", "-USDT");
            var rsp = await httpClient.UsdtFuturesApi.Trading.CancelCrossMarginOrderAsync(contractCode: contract, orderId: long.Parse(orderId));
            r.success = rsp.Success;
            if (!rsp.Success)
            {
                r.errMes = rsp.Error!.Message;
                Logger.Add(curName, $"{exName} {rsp.Error.Message}", LogType.Error);
            }
            return r;
        }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction)
        {
            Task.Run(async () =>
            {
                var sub = await socketClient.UsdtFuturesApi.SubscribeToTickerUpdatesAsync(crDt.name.Replace("USDT", "-USDT"), update =>
                {
                    crDt.askPrice = (double)update.Data.BestAsk.Price;
                    crDt.askAmount = (double)update.Data.BestAsk.Quantity;
                    crDt.bidPrice = (double)update.Data.BestBid.Price;
                    crDt.bidAmount = (double)update.Data.BestBid.Quantity;
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
                await socketClient.UsdtFuturesApi.SubscribeToOrderUpdatesAsync(MarginMode.Cross, update =>
                {
                    var trade = update.Data.Trade.FirstOrDefault();
                    if (trade == null) return;

                    var symbol = update.Data.ContractCode.Replace("-USDT", "USDT");
                    decimal step = meta.TryGetValue(symbol, out var m) ? m.Step : 1m;
                    decimal qty = (decimal)trade.Quantity * step;
                    decimal sign = update.Data.OrderSide == OrderSide.Buy ? 1m : -1m;
                    decimal signedQty = sign * qty;
                    bool noAlign = update.Data.ClientOrderId > 0;

                    var log = $"{exName} DEAL " +
                              $"TradeId: {trade.TradeId}  " +
                              $"Side: {update.Data.OrderSide}  " +
                              $"Qty: {qty}  " +
                              $"Price: {trade.Price}  " +
                              $"clientOrderId: {update.Data.ClientOrderId}  " +
                              $"QuoteQty: {trade.Value}";
                    Logger.Add(symbol, log, LogType.Info);

                    string asset = symbol.Replace("USDT", "");
                    generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + signedQty;

                    var key = symbol + "order";
                    if (!noAlign && wsSubscriptions.TryGetValue(key, out var act))
                        act(signedQty);
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
            string contract = crDt.name.Replace("USDT", "-USDT");
            var rsp = await httpClient.UsdtFuturesApi.Trading.CancelAllCrossMarginOrdersAsync(contract);
            if (rsp.Success || (rsp.Error?.Message?.Contains("No cancellable orders") ?? false))
                return true;
            Logger.Add(crDt.name, "CancelAllOrders: " + rsp.Error!.Message, LogType.Error);
            return false;
        }

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            string asset = crDt.name.Replace("USDT", "");
            generalBalance.TryAdd(asset, 0);
            var rsp = await httpClient.UsdtFuturesApi.Account.GetCrossMarginPositionsAsync();
            if (!rsp.Success)
            {
                Logger.Add(crDt.name, exName + " UpdateBalance: " + rsp.Error!.Message, LogType.Error);
                return false;
            }
            foreach (var p in rsp.Data)
            {
                string sym = p.Asset;
                decimal step = meta.TryGetValue(sym + "USDT", out var m) ? m.Step : 1m;
                decimal qty = (p.Side == OrderSide.Sell ? -1 : 1) * p.Quantity * step;
                generalBalance.AddOrUpdate(sym, qty, (k, v) => qty);
            }

            Logger.Add(crDt.name, "UpdateBalance " + exName + ": " + generalBalance[asset], LogType.Info);
            return true;
        }


        public override decimal GetBalance(CurData crDt)
        {
            string asset = crDt.name.Replace("USDT", "");
            return generalBalance.TryGetValue(asset, out var bal) ? bal : 0m;
        }

        public override async Task RefreshMetadataAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.hbdm.com/linear-swap-api/v1/swap_contract_info");
            var data = JsonConvert.DeserializeObject<dynamic>(ans)["data"];
            foreach (var c in data)
            {
                string curNm = c["contract_code"].ToString().Replace("-", "");
                decimal step = decimal.Parse(c["contract_size"].ToString(), CultureInfo.InvariantCulture);

                bool active = c["contract_status"] != null && ((int)c["contract_status"]) == 1;

                var m = new CoinMetaLocal {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    LastUpdateTm = DateTime.UtcNow,
                    FundingRate = 0,
                    PricePrecision = GetDecimalPlaces((double)c["price_tick"]),
                    Test = 0
                };
                //Console.WriteLine(m.PricePrecision);
                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }

    }

}
