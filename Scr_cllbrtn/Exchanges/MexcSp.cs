using CryptoExchange.Net.Authentication;
using Mexc.Net.Clients;
using Mexc.Net.Enums;
using Mexc.Net.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Collections.Generic;


namespace Scr_cllbrtn.Exchanges
{
    public class MexcSp : BaseExchange
    {
        MexcRestClient httpClientJKorf;
        MexcSocketClient socketClient;
        //protected HashSet<string> activePairs;
        Dictionary<string, string> unsubscribeId = new Dictionary<string, string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<OrderResult>> _fokAwaiters = new(StringComparer.OrdinalIgnoreCase);

        private double _totalUsdt;
        public override double TotalUsdt => Volatile.Read(ref _totalUsdt);

        public MexcSp()
        {
            httpClientJKorf = new MexcRestClient(options =>
            {
                //options.ApiCredentials = new KucoinApiCredentials("API-KEY", "API-SECRET", "API-PASSPHRASE");
                options.RequestTimeout = TimeSpan.FromSeconds(3);
                options.ApiCredentials = new ApiCredentials("mx0vgld7VkcchinCy1", "ef1f3afd7a7c4f8b9dd94b9ee6dee9f2");
                options.SpotOptions.OutputOriginalData = true;
                //options.FuturesOptions.AutoTimestamp = false;
            });

            socketClient = new MexcSocketClient(options =>
            {
                options.ApiCredentials = new ApiCredentials("mx0vgld7VkcchinCy1", "ef1f3afd7a7c4f8b9dd94b9ee6dee9f2");
                options.SpotOptions.OutputOriginalData = true;
            });

            UpdateBalanceAsync(new CurData(this, "BTCUSDT")).GetAwaiter().GetResult();
            //SubscribeToUpdateOrders();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.mexc.com/api/v3/ticker/bookTicker");
            return AnswerToDictionary(ans);
        }

        public async Task<string[]> LoadActivePairs()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.mexc.com/api/v3/defaultSymbols");
            Newtonsoft.Json.Linq.JArray item = JsonConvert.DeserializeObject<dynamic>(ans)["data"];
            return item.ToObject<string[]>();
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans))
            {
                CurData curData = new CurData(this, item["symbol"].ToString().ToUpper());
                if (item["askPrice"].ToString() == "" || item["bidPrice"].ToString() == "") { continue; }
                curData.askPrice = double.Parse(item["askPrice"].ToString());
                curData.bidPrice = double.Parse(item["bidPrice"].ToString());
                curData.askAmount = double.Parse(item["askQty"].ToString());
                curData.bidAmount = double.Parse(item["bidQty"].ToString());
                res[curData.name] = curData;
            }

            double totalBalanceInUsdt = 0;
            foreach (var (asset, balance) in generalBalance)
            {
                if (string.IsNullOrWhiteSpace(asset) || balance == 0)
                    continue;
                if (!res.TryGetValue(asset + "USDT", out var curData))
                    continue;
                totalBalanceInUsdt += (double)balance * curData.bidPrice;
            }
            //totalBalanceInUsdt += (double)generalBalance["USDT"];
            //Volatile.Write(ref _totalUsdt, totalBalanceInUsdt);
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
                "https://api.mexc.com/api/v3/depth?limit=7&symbol=" + curNm
            );
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans);
            if (jsonData == null)
                throw new Exception("JSON parse error");

            double tsVal = jsonData["timestamp"] != null ? jsonData["timestamp"].Value<double>() : 0.0;
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

            // Create CurData
            var curData = new CurData(this, curNm)
            {
                // Suppose you keep the balance
                // balance = balance[curNm],
                askPrice = askPrice,
                askAmount = askAmount,
                bidPrice = bidPrice,
                bidAmount = bidAmount,
                Timestamp = ts
            };

            return curData;
        }

        public override Task<OrderResult> BuyAsync(string c, decimal v, decimal p, bool noAlign, bool fok) => PlaceOrderAsync(OrderSide.Buy, c, v, p, noAlign, fok);
        public override Task<OrderResult> SellAsync(string c, decimal v, decimal p, bool noAlign, bool fok) => PlaceOrderAsync(OrderSide.Sell, c, v, p, noAlign, fok);
        private async Task<OrderResult> PlaceOrderAsync(OrderSide side, string curName, decimal vol, decimal price, bool noAlign, bool fok)
        {
            vol = Math.Abs(vol);
            Logger.Add(curName, $" Place {side} {exName} {vol} {price}", LogType.Info);

            var orderResult = new OrderResult(exName, curName);

            string cid = noAlign ? $"{tagNoAlign}{DateTime.UtcNow.Ticks}" : $"fok{DateTime.UtcNow.Ticks}";
            TaskCompletionSource<OrderResult>? tcs = null;
            if (fok)
            {
                tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _fokAwaiters[cid] = tcs;
            }

            var rsp = await httpClientJKorf.SpotApi.Trading.PlaceOrderAsync(
                            curName,
                            side,
                            price == 0 ? OrderType.Market : fok ? OrderType.FillOrKill : OrderType.Limit,
                            vol,
                            quoteQuantity: null,
                            price: price == 0 ? null : price,
                            clientOrderId: cid);

            if (!rsp.Success)
            {
                orderResult.errMes = rsp.Error.Message;
                if (fok) _fokAwaiters.TryRemove(cid, out _);
                orderResult.success = false;
                orderResult.errMes = rsp.Error!.Message;
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
                    orderId = rsp.Data.OrderId,
                    success = false,
                    errMes = "FOK expired: no fill from WS"
                };
            }

            orderResult.success = true;
            orderResult.orderId = rsp.Data.OrderId;

            return orderResult;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName) 
        {
            OrderResult orderResult = new OrderResult(exName, curName);
            orderResult.orderId = orderId;
            var positionResultData = await httpClientJKorf.SpotApi.Trading.CancelOrderAsync(curName, orderId);
            orderResult.success = positionResultData.Success;
            if (!orderResult.success) { orderResult.errMes = positionResultData.Error.Message; Logger.Add(curName, $"{exName} {positionResultData.Error.Message}", LogType.Error); }
            return orderResult;
        }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction)
        {
            Task.Run(async () =>
            {
                var r = await socketClient.SpotApi.SubscribeToBookTickerUpdatesAsync(crDt.name, update =>
                {
                    crDt.askPrice = (double)update.Data.BestAskPrice;
                    crDt.askAmount = (double)update.Data.BestAskQuantity;
                    crDt.bidPrice = (double)update.Data.BestBidPrice;
                    crDt.bidAmount = (double)update.Data.BestBidQuantity;
                    Logger.Add(crDt.name, $" Deal {exName} prA: {crDt.askPrice}, " +
                        $"prB: {crDt.bidPrice}, amA: {crDt.askAmount}, amB: {crDt.bidAmount};", LogType.Data);
                    updateAction(null);
                });
                Logger.Add(crDt.name, "Add unsubscribeId ", LogType.Info);
                unsubscribeId.Add("TickerID_" + crDt.name, r.Data.Id.ToString());
            }).Wait();
        }

        private void SubscribeToUpdateOrders()
        {
            Task.Run(async () =>
            {
                // 1. Get listen-key for user stream
                var listenKey = (await httpClientJKorf.SpotApi.Account.StartUserStreamAsync()).Data;

                // 2. Subscribe to user trades
                var res = await socketClient.SpotApi.SubscribeToUserTradeUpdatesAsync(listenKey, update =>
                {
                    // ----- LOG ----------------------------------------------------------
                    var log = $"{exName} DEAL " +
                              $"TradeId: {update.Data.TradeId}  " +
                              $"Side: {update.Data.TradeSide}  " +
                              $"Qty: {update.Data.Quantity}  " +
                              $"Price: {update.Data.Price}  " +
                              $"clientOrderId: {update.Data.ClientOrderId}  " +
                              $"QuoteQty: {update.Data.QuoteQuantity}  " +
                              $"Fee: {update.Data.Fee} {update.Data.FeeAsset}";
                    Logger.Add(update.Symbol, log, LogType.Info);
                    Console.WriteLine(log);

                    if (update.Data.ClientOrderId is { Length: > 0 } cid && _fokAwaiters.TryRemove(cid, out var waiter))
                    {
                        waiter.TrySetResult(new OrderResult(exName, update.Symbol)
                        {
                            orderId = update.Data.OrderId.ToString(),
                            success = true
                        });
                    }

                    bool noAlign = update.Data.ClientOrderId?.StartsWith(tagNoAlign, StringComparison.OrdinalIgnoreCase) == true;

                    // ----- Update balance --------------------------------------------
                    var asset = update.Symbol.Replace("USDT", "");
                    var sign = update.Data.TradeSide == OrderSide.Buy ? 1m : -1m;
                    generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + sign * update.Data.Quantity;

                    Console.WriteLine(generalBalance[asset]);
                    // ----- Custom callbacks (if needed) ------------------------------
                    if (sign < 0 && !noAlign && wsSubscriptions.TryGetValue(update.Symbol + "order", out var handler))
                        handler(sign * update.Data.Quantity);
                });
            }).Wait();
        }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
        {
            wsSubscriptions.Add(crDt.name + "order", filledAction);
        }

        public override void Unsubscribe(CurData crDt) 
        {
            if (!unsubscribeId.TryGetValue("TickerID_" + crDt.name, out var id))
            {
                Logger.Add(crDt.name, "Unsubscribe: key not found → skip", LogType.Info);
                return;                               // were not subscribed or already removed
            }

            Task.Run(async () =>
            {
                var r = await socketClient.SpotApi.UnsubscribeAsync(int.Parse(id));
            }).Wait();
            Task.Delay(1000).Wait();
            if (wsSubscriptions.ContainsKey(crDt.name + "order")) { wsSubscriptions.Remove(crDt.name + "order"); }
            if (wsSubscriptions.ContainsKey(crDt.name + "update")) { wsSubscriptions.Remove(crDt.name + "update"); }

            Logger.Add(crDt.name, "Remove unsubscribeId " + crDt.name, LogType.Info);
            unsubscribeId.Remove("TickerID_" + crDt.name);
        }

        public override async Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            var ansData = await httpClientJKorf.SpotApi.Trading.CancelAllOrdersAsync(crDt.name);
            if (ansData.Success) { return  true; }
            else
            {
                Logger.Add(crDt.name, "CancelAllOrders: " + ansData.Error.Message, LogType.Error);
                return false;
            }
        }

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            string nm = crDt.name.Replace("USDT", "");
            generalBalance.TryAdd(nm, 0);
            var ansData = await httpClientJKorf.SpotApi.Account.GetAccountInfoAsync();
            if (!ansData.Success) 
            {
                Logger.Add(crDt.name, exName + " UpdateBalance: " + ansData.Error.Message, LogType.Error);
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in ansData.Data.Balances)
            {
                seen.Add(item.Asset);
                generalBalance.AddOrUpdate(item.Asset, item.Total, (k, v) => item.Total);
            }

            foreach (var asset in generalBalance.Keys)
            {
                if (!seen.Contains(asset))
                {
                    generalBalance[asset] = 0m;
                }
            }
            Logger.Add(crDt.name, "UpdateBalance " + exName + ": " + generalBalance[nm], LogType.Info);
            return true;
        }

        public override decimal GetBalance(CurData crDt)
        {
            string nm = crDt.name.Replace("USDT", "");
            if (generalBalance.ContainsKey(nm)) { return generalBalance[nm]; }
            else { return 0; }
        }

        public override async Task RefreshMetadataAsync()
        {
            // Pairs allowed for our IP
            var ipJson = await SendApiRequestToExchangeAsync("https://api.mexc.com/api/v3/defaultSymbols");
            var ipPairs = JsonConvert.DeserializeObject<dynamic>(ipJson)?["data"].ToObject<HashSet<string>>() ?? new HashSet<string>();
            if (ipPairs.Count == 0) { throw new InvalidOperationException("MEXC defaultSymbols: empty list — metadata not updated"); }

            // Full description of all symbols
            var info = JsonConvert.DeserializeObject<dynamic>(await SendApiRequestToExchangeAsync("https://api.mexc.com/api/v3/exchangeInfo"));

            foreach (var c in info.symbols)
            {
                string curNm = c.symbol;

                var m = new CoinMeta {
                    Step = Math.Max((decimal)c.baseSizePrecision, (decimal)Math.Pow(10, -(int)c.baseAssetPrecision)),
                    Active = (c.status == "1") && ipPairs.Contains(curNm),
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    LastUpdateTm = DateTime.UtcNow,
                    MinOrderUSDT = 1
                };

                base.meta.AddOrUpdate(curNm = c.symbol, m, (_, __) => m);   // thread-safe

            }
        }

    }

}