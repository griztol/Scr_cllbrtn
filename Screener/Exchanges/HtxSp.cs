using CryptoExchange.Net.Authentication;
using HTX.Net;
using HTX.Net.Clients;
using HTX.Net.Enums;
using HTX.Net.Objects;
using Mexc.Net.Clients;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screener.Exchanges
{
    public class HtxSp : BaseExchange
    {

        HTXRestClient httpClientJKorf;
        HTXSocketClient socketClient;
        long accountId;
        Dictionary<string, string> unsubscribeId = new Dictionary<string, string>();


        //private static readonly HTXEnvironment AwsEnv = HTXEnvironment.CreateCustom(
        //    "aws-cn",                              // произвольное имя окружения
        //    "https://api-aws.huobi.pro",           // Spot REST-базовый URL
        //    "wss://api-aws.huobi.pro/ws",          // Spot WebSocket-базовый URL
        //    "https://api-aws.huobi.pro",           // USDT-margin-swap REST (можно тот же)
        //    "wss://api-aws.huobi.pro/ws"           // USDT-margin-swap WS (тоже тот же)
        //);

        public HtxSp() 
        {
            httpClientJKorf = new HTXRestClient(options =>
            {
                //options.Environment = AwsEnv;
                options.ApiCredentials = new ApiCredentials("be7759e0-b1ca883f-3d2xc4v5bu-c5d54", "a8086e8f-b946ab4e-d548e16c-8017a");
                //options.RequestTimeout = TimeSpan.FromSeconds(60);
                options.SpotOptions.OutputOriginalData = true;
                //options.FuturesOptions.AutoTimestamp = false;
            });

            socketClient = new HTXSocketClient(options =>
            {
                //options.Environment = AwsEnv;
                options.ApiCredentials = new ApiCredentials("be7759e0-b1ca883f-3d2xc4v5bu-c5d54", "a8086e8f-b946ab4e-d548e16c-8017a");
                options.SpotOptions.OutputOriginalData = true;
            });

            accountId = Task.Run(async () =>
            {
                var acc = await httpClientJKorf.SpotApi.Account.GetAccountsAsync();
                if (!acc.Success) throw new Exception(acc.Error!.Message);
                return acc.Data.Single(a => a.Type == AccountType.Spot).Id;
            }).Result;

            SubscribeToUpdateOrders();
        }

        public override async Task<Dictionary<string, CurData>> GetAllCurrenciesAsync()
        {
            string ans = await SendApiRequestToExchangeAsync("https://api-aws.huobi.pro/market/tickers");
            return AnswerToDictionary(ans);
        }

        protected override Dictionary<string, CurData> AnswerToDictionary(string ans)
        {
            Dictionary<string, CurData> res = new(StringComparer.OrdinalIgnoreCase);
            foreach (var item in JsonConvert.DeserializeObject<dynamic>(ans)["data"])
            {
                CurData curData = new CurData(this, item["symbol"].ToString().ToUpper());
                if (item["ask"].ToString() == "" || item["bid"].ToString() == "") { continue; }
                curData.askPrice = double.Parse(item["ask"].ToString());
                curData.bidPrice = double.Parse(item["bid"].ToString());
                curData.askAmount = double.Parse(item["askSize"].ToString());
                curData.bidAmount = double.Parse(item["bidSize"].ToString());
                res[curData.name] = curData;
            }
            //Logger.Add(exName + " " + res.Count);
            return res;
        }

        public override async Task<CurData> GetLastPriceAsync(string curNm)
        {
            string ans = await SendApiRequestToExchangeAsync(
                "https://api-aws.huobi.pro/market/depth?symbol=" + curNm.ToLower() + "&depth=5&&type=step0");
            Logger.Add(curNm, exName + " " + ans, LogType.Data);

            JObject? jsonData = JsonConvert.DeserializeObject<JObject>(ans);
            if (jsonData == null)
                throw new Exception("JSON parse error");

            double tsVal = jsonData["ts"] != null ? jsonData["ts"].Value<double>() : 0.0;
            DateTime ts = DateTimeOffset.FromUnixTimeMilliseconds((long)tsVal).LocalDateTime;

            var item = jsonData["tick"];
            var asksToken = item?["asks"] as JArray;
            var bidsToken = item?["bids"] as JArray;
            if (asksToken == null || bidsToken == null)
                throw new Exception("Invalid response: no asks/bids");

            List<double[]> asks = asksToken
                .Select(a => new double[] { a[0]!.Value<double>(), a[1]!.Value<double>() })
                .ToList();

            List<double[]> bids = bidsToken
                .Select(b => new double[] { b[0]!.Value<double>(), b[1]!.Value<double>() })
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

        public override Task<OrderResult> BuyAsync(string c, decimal v, decimal p, bool noAlign, bool fok) => PlaceOrderAsync(OrderSide.Buy, c, v, p, noAlign, fok);
        public override Task<OrderResult> SellAsync(string c, decimal v, decimal p, bool noAlign, bool fok) => PlaceOrderAsync(OrderSide.Sell, c, v, p, noAlign, fok);

        private async Task<OrderResult> PlaceOrderAsync(OrderSide side, string curName, decimal vol, decimal price, bool noAlign, bool fok)
        {
            price = decimal.Round(price, meta[curName].PricePrecision);
            vol = Math.Abs(vol);
            Logger.Add(curName, $" Place {side} {exName} {vol} {price}", LogType.Info);

            var result = new OrderResult(exName, curName);

            var rsp = await httpClientJKorf.SpotApi.Trading.PlaceOrderAsync(
                accountId,
                curName,
                side,
                price == 0 ? OrderType.Market : fok ? OrderType.FillOrKillLimit : OrderType.Limit,
                vol,
                price: price == 0 ? null : price,
                clientOrderId: noAlign ? $"{tagNoAlign}{DateTime.UtcNow.Ticks}" : null);

            result.success = rsp.Success;
            if (result.success) result.orderId = rsp.Data.ToString();
            else { result.errMes = rsp.Error.Message; Logger.Add(curName, $"{exName} {rsp.Error.Message}", LogType.Error); }
            return result;
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

        public override void SubscribeToUpdateOrders(CurData crDt, Action<object> filledAction)
        {
            wsSubscriptions.Add(crDt.name + "order", filledAction);
        }

        private void SubscribeToUpdateOrders()
        {
            Task.Run(async () =>
            {
                await socketClient.SpotApi.SubscribeToOrderUpdatesAsync(null, onOrderMatched: update =>
                {
                    // ----- LOG ----------------------------------------------------------
                    var log = $"{exName} DEAL  " +
                              $"TradeId: {update.Data.TradeId}  " +
                              $"Side: {update.Data.Side}  " +
                              $"Qty: {update.Data.TradeQuantity}  " +
                              $"Price: {update.Data.TradePrice}  " +
                              $"clientOrderId: {update.Data.ClientOrderId}  " +
                              $"QuoteQty: {update.Data.QuoteQuantity}";
                    Logger.Add(update.Symbol, log, LogType.Info);
                    Console.WriteLine(log);

                    bool noAlign = update.Data.ClientOrderId?.StartsWith(tagNoAlign, StringComparison.OrdinalIgnoreCase) == true;

                    // ----- Update balance --------------------------------------------
                    var asset = update.Symbol.Replace("USDT", "");
                    var sign = update.Data.Side == OrderSide.Buy ? 1m : -1m;
                    generalBalance[asset] = generalBalance.GetValueOrDefault(asset) + sign * update.Data.TradeQuantity;

                    Console.WriteLine(generalBalance[asset]);
                    // ----- Custom callbacks (if needed) ------------------------------
                    if (sign < 0 && !noAlign && wsSubscriptions.TryGetValue(update.Symbol + "order", out var handler))
                        handler(sign * update.Data.TradeQuantity);
                });
            }).Wait();
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

        public override async Task<bool> UpdateBalanceAsync(CurData crDt)
        {
            string nm = crDt.name.Replace("USDT", "");
            generalBalance.TryAdd(nm, 0);
            var ans = await httpClientJKorf.SpotApi.Account.GetBalancesAsync(accountId);
            if (!ans.Success) { Logger.Add(crDt.name, exName + " UpdateBalance: " + ans.Error.Message, LogType.Error); return false; }
            foreach (var item in ans.Data)
            {
                if (item.Type != BalanceType.Trade) { continue; }
                if (item.Asset.Equals(nm, StringComparison.OrdinalIgnoreCase))
                    generalBalance.AddOrUpdate(nm, item.Balance, (_, __) => item.Balance);
            }
            Logger.Add(crDt.name, "UpdateBalance " + exName + ": " + generalBalance[nm], LogType.Info);
            return true;
        }

        public override async Task<OrderResult> CancelOrderAsync(string orderId, string curName)
        {
            OrderResult result = new OrderResult(exName, curName) { orderId = orderId };
            var rsp = await httpClientJKorf.SpotApi.Trading.CancelOrderAsync(long.Parse(orderId));
            result.success = rsp.Success;
            if (!result.success) { result.errMes = rsp.Error.Message; Logger.Add(curName, $"{exName} {rsp.Error.Message}", LogType.Error); }
            return result;
        }

        public override async Task<bool> CancelAllOrdersAsync(CurData crDt)
        {
            var rsp = await httpClientJKorf.SpotApi.Trading.CancelAllOrdersAsync(crDt.name);
            if (rsp.Success) return true;
            Logger.Add(crDt.name, "CancelAllOrders: " + rsp.Error.Message, LogType.Error);
            return false;
        }

        public override decimal GetBalance(CurData crDt)
        {
            string nm = crDt.name.Replace("USDT", "");
            return generalBalance.GetValueOrDefault(nm);
        }

        public override async Task RefreshMetadataAsync()
        {
            var info = JsonConvert.DeserializeObject<dynamic>(await SendApiRequestToExchangeAsync("https://api-aws.huobi.pro/v1/common/symbols"));
            foreach (var c in info.data)
            {
                string curNm = ((string)c["base-currency"] + (string)c["quote-currency"]).ToUpper();
                var m = new CoinMeta {
                    Step = (decimal)Math.Pow(10, -(int)c["amount-precision"]),
                    Active = ((string)c["state"]) == "online",
                    InBlackList = meta.TryGetValue(curNm, out var b) ? b.InBlackList : false,
                    FundingRate = 0,
                    LastUpdateTm = DateTime.UtcNow,
                    PricePrecision = (byte)c["price-precision"]
                };
                base.meta.AddOrUpdate(curNm, m, (_, __) => m);
            }
        }

    }
}
