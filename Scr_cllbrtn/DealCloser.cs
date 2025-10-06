using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scr_cllbrtn
{
    public class DealCloser
    {
        decimal placeDelta = 0.001m;
        decimal timePenalty = 0;
        public CurData curSell;
        public CurData curBuy;
        public volatile bool _workDone = false;
        public volatile bool _resetCancelOrder = false;
        volatile bool _auditInProgress = false;
        DateTime _lastAdjust = DateTime.Now;
        private decimal _diffToAlign = 0m;
        readonly object _lock = new();
        
        Semaphore sem = new Semaphore(1, 1);
        Func<Task<OrderResult>> cancelOrder;
        public DealCloser(CurData cS, CurData cB)
        {
            Thread.Sleep(7000);
            this.curSell = cB;
            this.curBuy = cS;

            placeDelta = (decimal)(InOutPercent.GetCurrentThresholds(curSell, curBuy).outPrc / 100);

            Logger.Add(curBuy.name, $"PlaceDelta = " + placeDelta, LogType.Info);

            try
            {
                curBuy.prnt.UpdateBalanceAsync(curBuy).GetAwaiter().GetResult();
                curSell.prnt.UpdateBalanceAsync(curSell).GetAwaiter().GetResult();
                Thread.Sleep(2000);

                GlbConst.CheckUsdtFloor();

                curBuy.prnt.SubscribeToUpdateOrders(curBuy, ChangeDiffToAlign);
                curSell.prnt.SubscribeToUpdateOrders(curSell, ChangeDiffToAlign);
                curBuy.prnt.SubscribeToUpdatePrices(curBuy, UpdatePricesAsync);
                curSell.prnt.SubscribeToUpdatePrices(curSell, UpdatePricesAsync);
            }
            catch (Exception ex)
            {
                Logger.Add(curBuy.name, "DealCloser init error: " + ex.Message, LogType.Error);
                try { Close(); }
                catch (Exception e)
                {
                    // 3) note that something also went wrong during emergency Close()
                    Logger.Add(curBuy.name,
                        $"Close() threw: {e.Message}", LogType.Error);
                }
                return;
            }



            _diffToAlign = curSell.Balance + curBuy.Balance;

            if (curSell.Balance == 0 || curBuy.Balance == 0 || placeDelta > 100)
            {
                Thread.Sleep(1000);
                Logger.Add(curBuy.name, $"the balance at the opening of the deal = 0 or placeDelta > 100", LogType.Error);
                curBuy.AddToBlackList(); curSell.AddToBlackList();
                Close();
            }
            _ = Task.Run(BalancesWatchLoopAsync);
        }

        public void UpdateDelta(decimal balanceIncrease)
        {
            Thread.Sleep(9000);
            if (!sem.WaitOne(5000))
            {
                Logger.Add(curSell.name, "UpdateDelta timeOut", LogType.Error);
                return;
            }

            try
            {
                decimal balSell = curSell.prnt.GetBalance(curSell);
                decimal balBuy = curBuy.prnt.GetBalance(curBuy);
                decimal averageBal = (Math.Abs(balBuy) + balSell) / 2m;

                Logger.Add(curBuy.name, $"UpdateDelta balances: sell={balSell}, buy={balBuy}, average={averageBal}", LogType.Info);

                if (averageBal == 0)
                {
                    Logger.Add(curBuy.name, "UpdateDelta averageBal = 0", LogType.Error);
                    return;
                }

                decimal oldDelta = placeDelta;
                decimal newDelta = (decimal)(InOutPercent.GetCurrentThresholds(curSell, curBuy).outPrc / 100);

                placeDelta = oldDelta * ((averageBal - balanceIncrease) / averageBal) + newDelta * (balanceIncrease / averageBal);

                // log final delta adjustment
                Logger.Add(
                    curBuy.name,
                    $"Delta: {oldDelta} → {placeDelta}, "
                  + $"balanceIncrease: {balanceIncrease}, newDeltaRef: {newDelta}",
                    LogType.Info);
            }
            finally
            {
                sem.Release();
            }
        }
        static decimal MinQty(CurData c, double price)
        {
            decimal step = c.prnt.meta[c.name].Step;
            decimal minAmnt = (decimal)c.minOrderUSDT / (decimal)price;
            return Math.Max(step, minAmnt);
        }

        private decimal GetMaxAmount(CurData c, decimal orderPrice)
        {
            decimal balanceCoins = c.Balance;                 // may be < 0
            double absTotalUsd = (double)(Math.Abs(balanceCoins) * orderPrice);

            /* --- 1. too small — place nothing -------------------------------- */
            if (absTotalUsd < c.minOrderUSDT)
                return 0;

            /* --- 2. +25% to the step ----------- */
            if (absTotalUsd <= GlbConst.StepUsd)
                return balanceCoins;

            /* --- 3. otherwise split by the standard 10 USDT step ----------------------- */
            return Math.Sign(balanceCoins) * ((decimal)GlbConst.StepUsd / orderPrice);
        }

        async void UpdatePricesAsync(object p)
        {
            if (_auditInProgress || _workDone) { return; }
            if (!sem.WaitOne(1)) { return; }

            try
            {
                if (_resetCancelOrder && cancelOrder != null)
                {
                    _resetCancelOrder = false;
                    (await Task.WhenAll(curSell.prnt.CancelAllOrdersAsync(curSell), curBuy.prnt.CancelAllOrdersAsync(curBuy))).All(r => r);
                    cancelOrder = null;
                    await Task.Delay(1500);
                    return;
                }

                if (cancelOrder == null)
                {
                    if (curSell.askPrice / curBuy.askPrice > curSell.bidPrice / curBuy.bidPrice)
                    {
                        decimal orderPrice = (decimal)curBuy.askPrice * (1 + placeDelta - timePenalty);
                        Logger.Add(curSell.name, $"Place A {curSell.exchange} {orderPrice}", LogType.Action);
                        Logger.Add(curBuy.name, $"{curBuy.exchange} PendingPriceOut Buy: {curBuy.askPrice};", LogType.Info);
                        decimal maxAmount = GetMaxAmount(curSell, orderPrice);
                        if (maxAmount == 0) 
                        { 
                            Logger.Add(curSell.name, $"{curSell.exchange} PlaceMaxAmount = 0, start StartAuditAsync", LogType.Info);
                            await StartAuditAsync();
                            return; 
                        }
                        OrderResult r = await curSell.SellAsync(maxAmount, orderPrice);
                        Logger.Add(r.curName, $"{r.exName} {r.success}", LogType.Result);
                        // pause to allow cancel to process
                        await Task.Delay(100);

                        if (r.success)
                        {
                            cancelOrder = async () =>
                            {
                                if ((double)(orderPrice) / curBuy.askPrice < (1 + (double)placeDelta - 0.001) ||
                                    (double)(orderPrice) / curBuy.askPrice > (1 + (double)placeDelta + 0.001))
                                {
                                    Logger.Add(curSell.name, "Start cancel " + curSell.exchange, LogType.Action);
                                    return await curSell.prnt.CancelOrderAsync(r.orderId, r.curName);
                                }
                                else { return await Task.FromResult<OrderResult>(null); }
                            };
                        }
                        //else
                        //{
                        //    if (r.errMes.Contains("internal error"))
                        //    {
                        //        // Perform emergency cancellation and continue working
                        //        if (!await EmergencyCancelAll()) { StartAuditAsync(); }
                        //    }
                        //}
                        else { _ = StartAuditAsync(); }
                    }
                    else
                    {
                        decimal orderPrice = (decimal)curSell.bidPrice * (1 - placeDelta + timePenalty);
                        Logger.Add(curBuy.name, $"Place B {curBuy.exchange} {orderPrice}", LogType.Action);
                        Logger.Add(curSell.name, $"{curSell.exchange} PendingPriceOut Sell: {curSell.bidPrice};", LogType.Info);
                        decimal maxAmount = GetMaxAmount(curBuy, orderPrice);
                        if (maxAmount == 0)
                        {
                            Logger.Add(curBuy.name, $"{curBuy.exchange} PlaceMaxAmount = 0, start StartAuditAsync", LogType.Info);
                            await StartAuditAsync();
                            return;
                        }
                        OrderResult r = await curBuy.BuyAsync(maxAmount, orderPrice);
                        Logger.Add(r.curName, $"{r.exName} {r.success}", LogType.Result);

                        if (r.success)
                        {
                            cancelOrder = async () =>
                            {
                                if ((double)orderPrice / curSell.bidPrice < (1 - (double)placeDelta - 0.001) ||
                                    (double)orderPrice / curSell.bidPrice > (1 - (double)placeDelta + 0.001))
                                {
                                    Logger.Add(curSell.name, "Start cancel " + curBuy.exchange, LogType.Action);
                                    return await curBuy.prnt.CancelOrderAsync(r.orderId, r.curName);
                                }
                                else { return await Task.FromResult<OrderResult>(null); }
                            };
                        }
                        else { _ = StartAuditAsync(); }
                    }
                }
                else
                {
                    OrderResult orderResult = await cancelOrder();
                    if (orderResult != null)
                    {
                        Logger.Add(orderResult.curName, $" Cancel {orderResult.success}", LogType.Result);
                        if (!orderResult.success && !orderResult.errMes.Contains("ORDER_NOT_FOUND")) 
                        {
                            if (!await EmergencyCancelAll()) { _ = StartAuditAsync(); }
                        }

                        cancelOrder = null;
                        await Task.Delay(900);
                    }

                    if ((DateTime.Now - _lastAdjust).TotalMinutes >= GlbConst.WaitingTime)
                    {
                        timePenalty += 0.001m;
                        AssetLimitManager.Decrease(curBuy.name, GlbConst.StepUsd);
                        //curBuy.AddToBlackList(); curSell.AddToBlackList();
                        Logger.Add(curBuy.name, "timePenalty = " + timePenalty, LogType.Info);
                        //_ = File.AppendAllTextAsync(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + "\\BlackList.txt", curBuy.name + "\r\n");
                        _lastAdjust = DateTime.Now;
                    }
                }

            }
            catch (Exception ex)
            {
                // Log the error and continue execution
                Logger.Add(curBuy.name, "Error in UpdatePrices method: " + ex.Message, LogType.Error);
                GlbConst.StopWork();
            }
            finally
            {
                sem.Release();
            }

        }

        async void ChangeDiffToAlign(object p)
        {
            if (_auditInProgress) { return; }
            _resetCancelOrder = true;
            lock(_lock) _diffToAlign += (decimal)p;
            Logger.Add(curBuy.name, $"ChangeDiffToAlign: added {(decimal)p}, _diffToAlign={_diffToAlign}", LogType.Info);
            if (!await AlignBalancesAsync()) 
            {
                Logger.Add(curBuy.name, "ChangeDiffToAlign: AlignBalancesAsync returned false → starting audit", LogType.Action);
                _ = StartAuditAsync(); 
            }
        }

        async Task<bool> AlignBalancesAsync()
        {
            Logger.Add(curBuy.name, $"AlignBalancesAsync: entry _diffToAlign={_diffToAlign}", LogType.Action);
            decimal qty;
            lock (_lock)
            {
                qty = _diffToAlign > 0 ? curSell.StepRound(_diffToAlign) : curBuy.StepRound(_diffToAlign);
                decimal sideMin = qty > 0 ? MinQty(curSell, curSell.bidPrice) : MinQty(curBuy, curBuy.askPrice);
                if (qty == 0m || Math.Abs(qty) < sideMin)
                {
                    Logger.Add(curBuy.name, $"AlignBalancesAsync: qty<{sideMin} → skip", LogType.Info);
                    return true;        // tail smaller than min order — nothing to align
                }
                _diffToAlign -= qty;
            }

            Logger.Add(curBuy.name, $"AlignBalancesAsync: qty={qty}", LogType.Info);
            OrderResult r = qty > 0 ? await curSell.SellAsync(qty, noAlign: true) : await curBuy.BuyAsync(qty, noAlign: true);
            Logger.Add(r.curName, $"AlignBalancesAsync: result success={r.success}", LogType.Result);
            if (r.success)
            {
                if(!GlbConst.workStopped) timePenalty = 0;
                AssetLimitManager.IncreaseLinear(curBuy.name, (double)Math.Abs(qty) * curSell.bidPrice);
            }
                
            return r.success;
        }

        private async Task StartAuditAsync()
        {
            if (_auditInProgress) { return; }
            _auditInProgress = true;
            Logger.Add(curBuy.name, "Audit → BEGIN", LogType.Action);
            cancelOrder = null;
            await EmergencyCancelAll();
            await Task.Delay(1500);
            await curSell.prnt.UpdateBalanceAsync(curSell);
            await curBuy.prnt.UpdateBalanceAsync(curBuy);

            lock (_lock) _diffToAlign = curSell.Balance + curBuy.Balance;
            Logger.Add(curBuy.name, $"Audit: diffToAlign={_diffToAlign}", LogType.Info);

            decimal minSell = MinQty(curSell, curSell.askPrice); // Gate
            decimal minBuy = MinQty(curBuy, curBuy.askPrice); // MEXC

            bool alignTooSmall =
                   _diffToAlign == 0m ||   // tail is zero
                   (_diffToAlign > 0 && Math.Abs(curSell.StepRound(_diffToAlign)) < minSell) ||
                   (_diffToAlign < 0 && Math.Abs(curBuy.StepRound(_diffToAlign)) < minBuy);

            bool anyBalanceTiny = Math.Abs(curSell.Balance) <= minSell || Math.Abs(curBuy.Balance) <= minBuy;

            if (alignTooSmall && anyBalanceTiny)
            {
                Logger.Add(curBuy.name, "Audit: nothing to align and balance minimal → Close()", LogType.Info);
                Close();
                _auditInProgress = false;
                return;
            }

            if (!await AlignBalancesAsync()) 
            {
                Logger.Add(curBuy.name, "Audit: AlignBalancesAsync returned false → Close()", LogType.Error);
                curBuy.AddToBlackList(); curSell.AddToBlackList();
                Close();
            }

            _auditInProgress = false;
            Logger.Add(curBuy.name, "Audit → END", LogType.Info);
        }

        private async Task BalancesWatchLoopAsync()
        {
            const int BadLimit = 5;
            const int PauseSec = 5;

            int badCounter = 0;

            while (!_workDone)
            {
                decimal usdTail = Math.Abs((curSell.Balance + curBuy.Balance) * (decimal)curSell.askPrice);
                Logger.Add(curBuy.name, $"WatchLoop: usdTail={usdTail:F4}$  badCnt={badCounter}", LogType.Info);
                if (_diffToAlign > 0
                ? (curSell.StepRound(_diffToAlign) != 0m && curSell.StepRound(_diffToAlign) * (decimal)curSell.askPrice > 1.2m)
                : curBuy.StepRound(_diffToAlign) != 0m)
                {
                    if (++badCounter >= BadLimit)
                    {
                        Logger.Add(curBuy.name, "WatchLoop: step mismatch persists, starting audit", LogType.Error);
                        badCounter = 0;
                        await StartAuditAsync();
                    }
                }
                else { badCounter = 0; }
                await Task.Delay(TimeSpan.FromSeconds(PauseSec));
            }
        }

        private void Close()
        {
            Logger.Add(curBuy.name, "Close() → start", LogType.Action);

            _workDone = true;
            EmergencyCancelAll().GetAwaiter().GetResult();
            curBuy.prnt.Unsubscribe(curBuy);
            curSell.prnt.Unsubscribe(curSell);

            Logger.Add(curBuy.name, "Close() completed", LogType.Info);
        }

        private async Task<bool> EmergencyCancelAll()
        {
            cancelOrder = null;
            Logger.Add(curBuy.name, "Starting EmergencyCancelAll", LogType.Action);
            await Task.Delay(500);

            for (int i = 1; i <= 3; i++)
            {
                Logger.Add(curBuy.name, $"EmergencyCancelAll attempt {i}", LogType.Info);
                if (!(await Task.WhenAll(curSell.prnt.CancelAllOrdersAsync(curSell), curBuy.prnt.CancelAllOrdersAsync(curBuy))).All(r => r))
                { Logger.Add(curBuy.name, $"Cancel attempt {i} failed", LogType.Error); }
                else
                {
                    Logger.Add(curBuy.name, "Orders successfully canceled during EmergencyCancelAll.", LogType.Info);
                    return true;
                }

                if (i < 3) { await Task.Delay(1500); }
            }

            // If cancellation fails after 3 attempts, initiate emergency closing
            Logger.Add(curBuy.name, "Failed to cancel order after 3 attempts. Initiating EmergencyAll.", LogType.Error);
            return false;
        }

    }
}
