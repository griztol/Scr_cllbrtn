using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using CryptoExchange.Net.Authentication;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Scr_cllbrtn.Exchanges
{
    public class BinanceSp : BaseExchange
    {
        BinanceRestClient httpClient;
        BinanceSocketClient socketClient;
        Dictionary<string, int> unsubscribeId = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<OrderResult>> _fokAwaiters = new(StringComparer.OrdinalIgnoreCase);

        public BinanceSp()
        {
            httpClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = 
                    new ApiCredentials("YBOfyW2px9mMnf6Hmakig8mDaWSSpd4jJLQl3UNXd5kbrPqzwfSBJyou665U67JE", "XKjJquHHVjSiEcKl8jGuXz6lj4Nax3mid1xHoirKuq4tGlEvYqCQuadPsjjfVA1r");
                options.SpotOptions.OutputOriginalData = true;
            });

            socketClient = new BinanceSocketClient(options =>
            {
                options.ApiCredentials = 
                    new ApiCredentials("YBOfyW2px9mMnf6Hmakig8mDaWSSpd4jJLQl3UNXd5kbrPqzwfSBJyou665U67JE", "XKjJquHHVjSiEcKl8jGuXz6lj4Nax3mid1xHoirKuq4tGlEvYqCQuadPsjjfVA1r");
                options.SpotOptions.OutputOriginalData = true;
            });

            SubscribeToUpdateOrders();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.binance.com/api/v3/ticker/bookTicker");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans))
            {
                CurData curData = new CurData(this, (string)item.symbol);
                if (item.askPrice.ToString() == "" || item.bidPrice.ToString() == "")
                    continue;
                curData.askPrice = (double)item.askPrice;
                curData.bidPrice = (double)item.bidPrice;
                curData.askAmount = (double)item.askQty;
                curData.bidAmount = (double)item.bidQty;
                if (curData.askPrice == 0 && curData.bidPrice == 0) { continue; }
                res[curData.name] = curData;
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
                "https://api.binance.com/api/v3/depth?limit=5&symbol=" + curNm);
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans);
            if (jsonData == null)
                throw new Exception("JSON parse error");

            var asksToken = jsonData["asks"] as JArray;
            var bidsToken = jsonData["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0]!.Value<double>(), a[1]!.Value<double>() })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0]!.Value<double>(), b[1]!.Value<double>() })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, meta[curNm].MinOrderUSDT);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, meta[curNm].MinOrderUSDT);

            CurData curData = new CurData(this, curNm)
            {
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = DateTime.UtcNow
            };

            return curData;
        }

        public override Task<OrderResult> BuyAsync(string c, decimal v, decimal p, bool noAlign, bool fok) =>
            PlaceOrderAsync(OrderSide.Buy, c, v, p, noAlign, fok);

        public override Task<OrderResult> SellAsync(string c, decimal v, decimal p, bool noAlign, bool fok) =>
            PlaceOrderAsync(OrderSide.Sell, c, v, p, noAlign, fok);

        private async Task<OrderResult> PlaceOrderAsync(OrderSide side, string curName, decimal vol, decimal price, bool noAlign, bool fok)
        {
            vol =  Math.Floor(Math.Abs(vol / meta[curName].Step)) * meta[curName].Step;
            price = Math.Round(price, meta[curName].PricePrecision);
            Logger.Add(curName, $" Place {side} {exName} {vol} {price}", LogType.Info);

            var orderResult = new OrderResult(exName, curName);

            string cid = noAlign ? $"{tagNoAlign}{DateTime.UtcNow.Ticks}" : $"fok{DateTime.UtcNow.Ticks}";
            TaskCompletionSource<OrderResult>? tcs = null;
            if (fok)
            {
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _fokAwaiters[cid] = tcs;
            }

            var rsp = await httpClient.SpotApi.Trading.PlaceOrderAsync(
                curName,
                side,
                price == 0 ? SpotOrderType.Market : SpotOrderType.Limit,
                vol,
                price: price == 0 ? null : price,
                timeInForce: price == 0 ? null : fok ? TimeInForce.FillOrKill : TimeInForce.GoodTillCanceled,
                newClientOrderId: cid);

            if (!rsp.Success)
            {
                orderResult.errMes = rsp.Error!.Message;
                if (fok) _fokAwaiters.TryRemove(cid, out _);
                Logger.Add(curName, $"{exName} {rsp.Error.Message}", LogType.Error);
                return orderResult;
            }

            if (fok)
            {
                var done = await Task.WhenAny(tcs!.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                if (done == tcs.Task)
                    return tcs.Task.Result;

                _fokAwaiters.TryRemove(cid, out _);
                return new OrderResult(exName, curName)
                {
                    orderId = rsp.Data.Id.ToString(),
                    success = false,
                    errMes = "FOK expired: no fill from WS"
                };
            }

            orderResult.success = true;
            orderResult.orderId = rsp.Data.Id.ToString();
            return orderResult;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName)
        {
            OrderResult r = new OrderResult(exName, curName) { orderId = orderId };
            var rsp = await httpClient.SpotApi.Trading.CancelOrderAsync(curName, orderId: long.Parse(orderId));
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
                var sub = await socketClient.SpotApi.ExchangeData.SubscribeToBookTickerUpdatesAsync(crDt.name, data =>
                {
                    crDt.askPrice = (double)data.Data.BestAskPrice;
                    crDt.askAmount = (double)data.Data.BestAskQuantity;
                    crDt.bidPrice = (double)data.Data.BestBidPrice;
                    crDt.bidAmount = (double)data.Data.BestBidQuantity;

                    Logger.Add(crDt.name,
                        $" Deal {exName} prA: {crDt.askPrice}, prB: {crDt.bidPrice}, amA: {crDt.askAmount}, amB: {crDt.bidAmount};",
                        LogType.Data);
                    updateAction(null);
                });

                if (sub.Success)
                    unsubscribeId["TickerID_" + crDt.name] = sub.Data.Id; // int, íå ñòðîêà
                else
                    Logger.Add(crDt.name, "SubscribeToBookTickerUpdatesAsync: " + sub.Error?.Message, LogType.Error);
            }).Wait();
        }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
        {
            wsSubscriptions[crDt.name + "order"] = filledAction;
        }

        private void SubscribeToUpdateOrders()
        {
            Task.Run(async () =>
            {
                // 1. Get listen-key for user stream
                var listenKey = (await httpClient.SpotApi.Account.StartUserStreamAsync()).Data;

                // 2. Subscribe to user trades
                var res = await socketClient.SpotApi.Account. SubscribeToUserDataUpdatesAsync(listenKey, update =>
                {
                    if (update.Data.TradeId <= 0)
                        return;

                    if (update.Data.LastQuantityFilled <= 0m)
                        return;

                    var log = $"{exName} DEAL  " +
                              $"TradeId: {update.Data.TradeId}  " +
                              $"Side: {update.Data.Side}  " +
                              $"Qty: {update.Data.Quantity}  " +
                              $"Price: {update.Data.Price}  " +
                              $"clientOrderId: {update.Data.ClientOrderId}  " +
                              $"QuoteQty: {update.Data.QuoteQuantity}  " +
                              $"Fee: {update.Data.Fee} {update.Data.FeeAsset}";
                    Logger.Add(update.Symbol, log, LogType.Info);
                    Console.WriteLine(log);

                    bool noAlign = update.Data.ClientOrderId?.StartsWith(tagNoAlign, StringComparison.OrdinalIgnoreCase) == true;

                    // ----- Update balance --------------------------------------------
                    var asset = update.Symbol.Replace("USDT", "");
                    var sign = update.Data.Side == OrderSide.Buy ? 1m : -1m;
                    generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + sign * update.Data.Quantity;

                    // ----- Custom callbacks (if needed) ------------------------------
                    if (sign < 0 && !noAlign && wsSubscriptions.TryGetValue(update.Symbol + "order", out var handler))
                        handler(sign * update.Data.Quantity);
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
            var rsp = await httpClient.SpotApi.Trading.CancelAllOrdersAsync(crDt.name);
            if (rsp.Success || rsp.Error?.Code == -2011) return true;
            Logger.Add(crDt.name, "CancelAllOrders: " + rsp.Error!.Message, LogType.Error);
            return false;
        }

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            string asset = crDt.name.Replace("USDT", "");
            generalBalance.TryAdd(asset, 0);
            var rsp = await httpClient.SpotApi.Account.GetAccountInfoAsync();
            if (!rsp.Success)
            {
                Logger.Add(crDt.name, exName + " UpdateBalance: " + rsp.Error!.Message, LogType.Error);
                return false;
            }
            foreach (var b in rsp.Data.Balances)
            {
                if (b.Asset == asset)
                    generalBalance.AddOrUpdate(asset, b.Available, (k, v) => b.Available);
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
            var info = JsonConvert.DeserializeObject<dynamic>(
                await SendApiRequestToExchangeAsync("https://api.binance.com/api/v3/exchangeInfo?symbolStatus=TRADING&permissions=SPOT&showPermissionSets=false"));

            foreach (var c in info.symbols)
            {
                string curNm = c.symbol.ToString();
                if (!curNm.Contains("USDT")) { continue; }
                decimal step = 0.00000001m;
                byte precision = 9;

                // NEW: íàêîïèì ìèíèìàëüíûé íîóøåíàë èç ôèëüòðà NOTIONAL/MIN_NOTIONAL
                decimal minNotional = 0m;

                foreach (var f in c.filters)
                {
                    string ft = (string)f.filterType;

                    if (ft == "LOT_SIZE")
                    {
                        step = decimal.Parse((string)f.stepSize, CultureInfo.InvariantCulture);
                    }
                    else if (ft == "PRICE_FILTER")
                    {
                        precision = GetDecimalPlaces((double)f.tickSize);
                    }
                    else if (ft == "NOTIONAL" || ft == "MIN_NOTIONAL") // NEW
                    {
                        // ó Binance ñåé÷àñ èñïîëüçóåòñÿ NOTIONAL; MIN_NOTIONAL  èñòîðè÷åñêèé âàðèàíò
                        minNotional = decimal.Parse((string)f.minNotional, CultureInfo.InvariantCulture);
                    }
                }

                bool active = c.status != null && ((string)c.status).Equals("TRADING", StringComparison.OrdinalIgnoreCase);

                var m = new CoinMeta
                {
                    Step = step,
                    Active = active,
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = precision,
                    MinOrderUSDT = (double)minNotional
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }
    }
}
