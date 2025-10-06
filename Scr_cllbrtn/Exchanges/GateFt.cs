using Io.Gate.GateApi.Api;
using Io.Gate.GateApi.Client;
using Io.Gate.GateApi.Model;
using Mexc.Net.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Globalization;

namespace Scr_cllbrtn.Exchanges
{
    public class GateFt : BaseExchange
    {
        //public static ConcurrentDictionary <string, decimal> contractMultiplier { get; } = new();
        FuturesApi clientGate;
        ClientWebSocket wsClient = new ClientWebSocket();

        public override double TotalUsdt
        {
            get => GetUnifiedTotalUsdtAsync().GetAwaiter().GetResult();
        }

        public GateFt()
        {
            Configuration config = new Configuration();
            config.Timeout = 1900;
            config.BasePath = "https://api.gateio.ws/api/v4";
            //config.BasePath = "https://fx-api.gateio.ws/api/v4";
            config.SetGateApiV4KeyPair("d314fcc862d49397bc06e99e8da9105e", "ab549373e239a2c94c3e6257e3213e9c41d23cdaa482d17b43e21d3c204f529b");
            clientGate = new FuturesApi(config);

            Task.Run(SocketListener, CancellationToken.None).Wait();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api.gateio.ws/api/v4/futures/usdt/tickers");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans))
            {
                CurData curData = new CurData(this, item["contract"].ToString().Replace("_", ""));
                if (item["lowest_ask"].ToString() == "" || item["highest_bid"].ToString() == "") { continue; }
                //curData.exchange = exName;
                curData.askPrice = double.Parse(item["lowest_ask"].ToString());
                curData.bidPrice = double.Parse(item["highest_bid"].ToString());
                //curData.askAmount = double.Parse(item["ask1Size"].ToString());
                //curData.bidAmount = double.Parse(item["bid1Size"].ToString());
                res[curData.name] = curData;
            }
            //Logger.Add(exName + " " + res.Count);
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string ans = await SendApiRequestToExchangeAsync(
                "https://fx-api.gateio.ws/api/v4/futures/usdt/order_book?limit=3&contract="
                + curNm.Replace("USDT", "_USDT")
            );

            // Parse JSON
            JObject? item = JsonConvert.DeserializeObject<JObject>(ans);
            Logger.Add(curNm, exName + " " + ans, LogType.Data);


            double tsVal = item?["current"] != null ? item["current"].Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)(tsVal * 1000)).LocalDateTime;

            // Assert that the asks/bids array exists
            var asksToken = item?["asks"] as JArray;
            var bidsToken = item?["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            double multiplier = (double)meta[curNm].Step;

            List<double[]> asks = asksToken
                .Select(a => new double[]
                {
                    a.Value<double>("p"),
                    a.Value<double>("s") * multiplier
                })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[]
                {
                    b.Value<double>("p"),
                    b.Value<double>("s") * multiplier
                })
                .ToList();

            var (askPrice, askAmount) = CalculatePriceWithFirstLevelAlwaysTaken(asks, (double)GlbConst.StepUsd);
            var (bidPrice, bidAmount) = CalculatePriceWithFirstLevelAlwaysTaken(bids, (double)GlbConst.StepUsd);

            // Create final CurData
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
            PlaceOrderAsync(c, Math.Abs(v), p, noAlign);

        public override Task<OrderResult> SellAsync(string c, decimal v, decimal p, bool noAlign, bool fok) =>
            PlaceOrderAsync(c, -Math.Abs(v), p, noAlign);
        async Task<OrderResult> PlaceOrderAsync(string cur, decimal volSigned, decimal price, bool noAlign)
        {
            Logger.Add(cur, $" Place {(volSigned > 0 ? "Buy" : "Sell")} {exName} {volSigned} {price}", LogType.Info);

            var res = new OrderResult(exName, cur);
            price = Math.Round(price, 12);
            decimal step = meta[cur].Step;

            /* — convert coins → contracts and set Size sign — */
            long size = (long)(Math.Abs(volSigned) / step);
            if (volSigned < 0) size *= -1;            // Gate: short ⇒ negative Size

            var order = new FuturesOrder(cur.Replace("USDT", "_USDT"))
            {
                Size = size,
                Price = price.ToString(),
                Tif = price == 0
                           ? FuturesOrder.TifEnum.Ioc      // market
                           : FuturesOrder.TifEnum.Gtc,     // limit
                Text = noAlign ? $"t-{tagNoAlign}" : string.Empty
                //ReduceOnly = volSigned > 0
            };

            try
            {
                var rsp = await clientGate.CreateFuturesOrderAsync("usdt", order);
                res.success = true;
                res.orderId = rsp.Id.ToString();
            }
            catch (Exception e)
            {
                res.success = false;
                res.errMes = e.Message;
                Logger.Add(cur, $"{exName} {e.Message}", LogType.Error);
            }

            return res;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName)
        {
            OrderResult orderResult = new OrderResult(exName, curName);
            orderResult.orderId = orderId;

            FuturesOrder orderResponse;
            try
            {
                orderResponse = await clientGate.CancelFuturesOrderAsync("usdt", orderId);
                orderResult.success = true;
            }
            catch (Exception e)
            {
                orderResult.success = false;
                orderResult.errMes = e.Message;
                Logger.Add(curName, $"{exName} {e.Message}", LogType.Error);
            }

            return orderResult;
        }

        async Task ConnectAsync()
        {
            try
            {
                if (wsClient.State != WebSocketState.Open && wsClient.State != WebSocketState.Connecting)
                {
                    wsClient = new ClientWebSocket();
                    await wsClient.ConnectAsync(new Uri("wss://fx-ws.gateio.ws/v4/ws/usdt"), CancellationToken.None);
                    SubscribeToUpdateOrders();
                    foreach (string item in wsSubscriptions.Keys)
                    {
                        if (item.Contains("update"))
                        {
                            string sendData = "{\"channel\" : \"futures.book_ticker\",\"event\": \"subscribe\", \"payload\" : [\"" +
                                item.Replace("update", "").Replace("USDT", "_USDT") + "\"]}";
                            Task.Run(async () =>
                            {
                                await wsClient.SendAsync(Encoding.UTF8.GetBytes(sendData), WebSocketMessageType.Text, true, CancellationToken.None);
                            }).Wait();
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Add(null, exName + " WebSocket connection " + ex.Message, LogType.Error); await Task.Delay(1500); }
        }

        async void SocketListener()
        {
            byte[] buf = new byte[1024];
            while (true)
            {
                try
                {
                    if (wsClient.State != WebSocketState.Open) { await ConnectAsync(); }
                    WebSocketReceiveResult receiveResult = await wsClient.ReceiveAsync(buf, CancellationToken.None);
                    AnalyzeReceivedMesssage(Encoding.UTF8.GetString(buf, 0, receiveResult.Count));
                }
                catch (WebSocketException ex) { Logger.Add(null, exName + " WebSocket " + ex.Message, LogType.Error); await Task.Delay(1500); }
                catch (Exception ex) { Logger.Add(null, exName + " WebSocket unknown " + ex.Message, LogType.Error); await Task.Delay(1500); }
            }
        }

        async void AnalyzeReceivedMesssage(string m)
        {
            if (m.Contains("futures.book_ticker\",\"event\":\"update"))
            {
                var ans = JsonConvert.DeserializeObject<dynamic>(m);
                CurData crDt = new CurData(this, ans["result"]["s"].ToString().Replace("_", ""));
                crDt.askPrice = (double)ans["result"]["a"];
                crDt.askAmount = ans["result"]["A"] * (double)meta[crDt.name].Step;
                crDt.bidPrice = (double)ans["result"]["b"];
                crDt.bidAmount = ans["result"]["B"] * (double)meta[crDt.name].Step;
                if (wsSubscriptions.ContainsKey(crDt.name + "update")) { wsSubscriptions[crDt.name + "update"](crDt); }
                else { Logger.Add(crDt.name, "wsSubscriptions don't contains key: " + crDt.name + "update", LogType.Error); }
            }
            else if (m.Contains("futures.book_ticker\",\"event")) { }
            else if (m.Contains("channel\":\"futures.usertrades\",\"event\":\"subscribe")) { }
            else if (m.Contains("channel\":\"futures.usertrades\",\"event\":\"update"))
            {
                var ans = JsonConvert.DeserializeObject<dynamic>(m);

                foreach (var t in ans["result"])          // process the array just in case
                {
                    string contract = t["contract"].ToString();   // BTC_USDT
                    string asset = contract.Replace("_", "").Replace("USDT", ""); // BTC
                    decimal size = (decimal)t["size"];         // + buy, − sell
                    decimal qty = size * meta[contract.Replace("_", "")].Step;
                    bool noAlign = t["text"]?.ToString().StartsWith($"t-{tagNoAlign}", StringComparison.OrdinalIgnoreCase) == true;

                    // log
                    string log = $"{exName} DEAL " +
                                 $"TradeId: ??? " +
                                 $"Side: {(size > 0 ? "BUY" : "SELL")}  " +
                                 $"Qty: {qty}  " +
                                 $"Price: {t["price"]}  " + 
                                 $"NoAlign: {noAlign}";
                    Logger.Add(contract.Replace("_", ""), log, LogType.Info);

                    // balance (+ on buy, − on sell)
                    generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + qty;


                    // callback if needed
                    if (qty > 0 && !noAlign && wsSubscriptions.TryGetValue(contract.Replace("_", "") + "order", out var handler))
                        handler(qty);
                }
            }
            else { throw new Exception(exName + " WebSocket uncknown message: " + m); }
            //Console.WriteLine(m);
        }

        public override void SubscribeToUpdatePrices(CurData crDt, Action<object> updateAction)
        {
            wsSubscriptions.Add(crDt.name + "update", update =>
            {
                CurData u = (CurData)update;
                crDt.askPrice = u.askPrice;
                crDt.askAmount = u.askAmount;
                crDt.bidPrice = u.bidPrice;
                crDt.bidAmount = u.bidAmount;
                Logger.Add(crDt.name, $" Deal {exName} prA: {crDt.askPrice}, " +
                    $"prB: {crDt.bidPrice}, amA: {crDt.askAmount}, amB: {crDt.bidAmount};", LogType.Data);
                updateAction(null);
            });
            string sendData = "{\"channel\" : \"futures.book_ticker\",\"event\": \"subscribe\", \"payload\" : [\"" + crDt.name.Replace("USDT", "_USDT") + "\"]}";
            //string sendData = "{\"channel\" : \"futures.order_book_update\",\"event\": \"subscribe\", \"payload\" : [\"" + crDt.name.Replace("USDT", "_USDT") + "\", \"100ms\", \"20\"]}";
            Task.Run(async () =>
            {
                await wsClient.SendAsync(Encoding.UTF8.GetBytes(sendData), WebSocketMessageType.Text, true, CancellationToken.None);
            }).Wait();
        }

        private void SubscribeToUpdateOrders()
        {
            string SignMsg(string msg)
            {
                using var hmac = new HMACSHA512(
                    Encoding.UTF8.GetBytes("ab549373e239a2c94c3e6257e3213e9c41d23cdaa482d17b43e21d3c204f529b"));
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(msg)))
                                   .Replace("-", "")
                                   .ToLowerInvariant();
            }

            // ── 1. timestamp ────────────────────────────────────────────────────────
            string ts = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

            // ── 2. signature for usertrades ───────────────────────────────────────────
            string sign = SignMsg($"channel=futures.usertrades&event=subscribe&time={ts}");

            // ── 3. JSON request (single line!) ───────────────────────────────────────
            string req =
                $"{{\"time\":{ts},\"channel\":\"futures.usertrades\",\"event\":\"subscribe\"," +
                $"\"payload\":[\"16200213\",\"!all\"]," +                                      // UID + all contracts
                $"\"auth\":{{\"method\":\"api_key\",\"KEY\":\"d314fcc862d49397bc06e99e8da9105e\",\"SIGN\":\"{sign}\"}}}}";

            // ── 4. send ───────────────────────────────────────────────────────
            Task
                .Run(async () =>
                    await wsClient.SendAsync(
                        Encoding.UTF8.GetBytes(req),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None))
                .Wait();
        }

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
        {
            wsSubscriptions.Add(crDt.name + "order", filledAction);
        }

        public override void Unsubscribe(CurData crDt)
        {
            string sendData = "{\"channel\" : \"futures.book_ticker\",\"event\": \"unsubscribe\", \"payload\" : [\"" + crDt.name.Replace("USDT", "_USDT") + "\"]}";
            Task.Run(async () =>
            {
                await wsClient.SendAsync(Encoding.UTF8.GetBytes(sendData), WebSocketMessageType.Text, true, CancellationToken.None);
            }).Wait();
            Task.Delay(1000).Wait();
            if (wsSubscriptions.ContainsKey(crDt.name + "order")) { wsSubscriptions.Remove(crDt.name + "order"); }
            if (wsSubscriptions.ContainsKey(crDt.name + "update")) { wsSubscriptions.Remove(crDt.name + "update"); }
        }

        public override async Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            List<FuturesOrder> orderResponse;
            try
            {
                orderResponse = await clientGate.CancelFuturesOrdersAsync("usdt", crDt.name.Replace("USDT", "_USDT"));
                return true;
            }
            catch (Exception e)
            {
                Logger.Add(crDt.name, "CancelAllOrders: " + e.Message, LogType.Error);
                return false;
            }
        }

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            string nm = crDt.name.Replace("USDT", "");
            generalBalance.TryAdd(nm, 0);
            List<Position> ansData;
            try
            {
                ansData = await clientGate.ListPositionsAsync("usdt", true);
            }
            catch (Exception e)
            {
                Logger.Add(crDt.name, exName + " UpdateBalance: " + e.Message, LogType.Error);
                return false;
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ansData)
            {
                string asset = item.Contract.Replace("_USDT", "");
                decimal bDec = item.Size * meta[item.Contract.Replace("_", "")].Step;
                generalBalance.AddOrUpdate(asset, bDec, (k, v) => bDec);
                seen.Add(asset);
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

        //public async Task<decimal> GetTotalUSDTAsync()
        //{
        //    try
        //    {
        //        // GET /futures/usdt/accounts
        //        var acc = await clientGate.ListFuturesAccountsAsync("usdt");

        //        var totalStr = acc?.Total ?? "0";
        //        return decimal.TryParse(totalStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
        //            ? d
        //            : 0m;
        //        Console.WriteLine(acc);
        //    }
        //    catch (Exception e)
        //    {
        //        Logger.Add(null, $"{exName} GetTotalUSDT: {e.Message}", LogType.Error);
        //        return 0m;
        //    }
        //}

        public async Task<double> GetUnifiedTotalUsdtAsync()
        {
            try
            {
                var wallet = new WalletApi(clientGate.Configuration.BasePath)
                {
                    Configuration = clientGate.Configuration
                };

                var resp = await wallet.GetTotalBalanceAsync("USDT");
                if (resp?.Total == null) return 0;

                return double.Parse(resp.Total.Amount) + double.Parse(resp.Total.UnrealisedPnl);
            }
            catch (Exception e)
            {
                Logger.Add(null, $"{exName} GetUnifiedTotalUsdt: {e.Message}", LogType.Error);
                return 0;
            }
        }

        public override async Task RefreshMetadataAsync()
        {
            var arr = JsonConvert.DeserializeObject<dynamic>(await SendApiRequestToExchangeAsync("https://fx-api.gateio.ws/api/v4/futures/usdt/contracts"));

            foreach (var c in arr)
            {
                string curNm = ((string)c.name).Replace("_", "");
                var m = new CoinMeta {
                    Step = (decimal)c.quanto_multiplier,
                    Active = (string)c.in_delisting != "True",
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = (double)c.funding_rate * 100,
                    LastUpdateTm = DateTime.Now,
                    MinOrderUSDT = 1
                };

                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }

    }

}
