using Scr_cllbrtn.Exchanges;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scr_cllbrtn
{
    public class DealOpener
    {
        public async Task<DealCloser> MakeDealAsync(CurData curBuy, CurData curSell)
        {

            //if (!curSell.exchange.Contains("Ft")) { return null; }
            Task<CurData> tS = curSell.UpdateAsync();
            Task<CurData> tB = curBuy.UpdateAsync();
            try { await tS; await tB; }
            catch (Exception) { Logger.Add(curBuy.name, "Update_exception", LogType.Info); return null; }
            if (tS.IsCanceled || tB.IsCanceled) { Logger.Add(curBuy.name, "Canceled", LogType.Info); return null; }

            curSell = tS.Result;
            curBuy = tB.Result;
            
            if (!IsReadyForDeal(curSell, curBuy)) { return null; }



            decimal amount = 0;
            try
            {
                amount = GetUnifiedAmount(curBuy, curSell);
                if (amount == 0)
                {
                    Logger.Add(curBuy.name, "UnifiedAmount = 0", LogType.Info);
                    return null;
                }
            }
            catch (Exception)
            {
                Logger.Add(curBuy.name, "error in UnifiedAmount", LogType.Info);
                return null;
            }

            LogPotentialDeal(curBuy, curSell);
            return new DealCloser(curSell, curBuy);

            Logger.Add(curBuy.name, $"{curBuy.exchange} PendingPriceIn Buy: {curBuy.askPrice};", LogType.Info);
            Logger.Add(curSell.name, $"{curSell.exchange} PendingPriceIn Sell: {curSell.bidPrice};", LogType.Info);

            Task<OrderResult> rB = curBuy.BuyAsync(amount, (decimal)(curBuy.askPrice * 1.0015), fok: true);
            await rB;
            if (!rB.Result.success)
            {
                Logger.Add(curBuy.name, "Buy failed " + rB.Result.errMes, LogType.Info);
                return null;
            }
            Task<OrderResult> rS = curSell.SellAsync(amount);
            await rS;

            //Task<OrderResult> rS = curSell.SellAsync(amount);
            //Task<OrderResult> rB = curBuy.BuyAsync(amount);
            //await rS; await rB;


            var deal = GlbConst.deals.FirstOrDefault(d => d.curSell.name == curSell.name);
            if (rS.Result.success && rB.Result.success) 
            {
                Logger.Add(curBuy.name, $"Deal Buy: p{curBuy.askPrice}, a{curBuy.askAmount}; Sell: p{curSell.bidPrice}, a{curSell.bidAmount};", LogType.Result);

                if (deal != null)
                {
                    deal.UpdateDelta(amount);
                    return null;
                } 
                else { return new DealCloser(curSell, curBuy); }
            }
            else
            {
                Logger.Add(curBuy.name, "Make deal " + rS.Result.errMes + "; " + rB.Result.errMes, LogType.Error);
                curBuy.AddToBlackList(); curSell.AddToBlackList();

                // ─── NO position → both orders rejected ───
                if (!rS.Result.success && !rB.Result.success)
                    return null;

                // ─── POSITION EXISTS but closer is already running ───
                if (deal != null)
                    return null;

                // ─── POSITION EXISTS and closer not yet started ───
                return new DealCloser(curSell, curBuy);
            }

        }

        private decimal GetUnifiedAmount(CurData curBuy, CurData curSell)
        {
            // 1) Lot steps -----------------------------------------------------------
            decimal stepBuy = curBuy.prnt.meta[curBuy.name].Step;
            decimal stepSell = curSell.prnt.meta[curSell.name].Step;
            decimal commonStep;

            if (stepBuy >= stepSell && stepBuy % stepSell == 0m) commonStep = stepBuy;
            else if (stepSell > stepBuy && stepSell % stepBuy == 0m) commonStep = stepSell;
            else
            {
                Logger.Add(curBuy.name, $"Incompatible lot steps: buy={stepBuy} sell={stepSell}", LogType.Error);
                return 0;                       // steps don't match — skip deal
            }

            // 2) Raw volume limited by order books -----------------------------------
            decimal amount = Math.Min((decimal)curBuy.askAmount, (decimal)curSell.bidAmount);

            // 3) USD cap ----------------------------------------------------
            decimal coinsByUsd = (decimal)(GlbConst.StepUsd / curBuy.askPrice);
            if ((double)amount * curBuy.askPrice > GlbConst.StepUsd)
                amount = coinsByUsd;

            // 4) Single rounding to commonStep ---------------------------------------
            amount = Math.Floor(amount / commonStep) * commonStep;

            if (amount == 0m) return 0;

            // 5) Minimum USD threshold ------------------------------------
            double amountUsd = (double)amount * curBuy.askPrice;
            return amountUsd < curBuy.minOrderUSDT || amountUsd < curSell.minOrderUSDT ? 0 : amount;
        }

        public bool IsReadyForDeal(CurData cS, CurData cB)
        {
            if (cS == null || cB == null) return false;

            bool enoughBuy = cB.askPrice * cB.askAmount + 1 >= GlbConst.LiquidityCheckUsd;
            bool enoughSell = cS.bidPrice * cS.bidAmount + 1 >= GlbConst.LiquidityCheckUsd;
            if (!enoughBuy || !enoughSell)
            {
                Logger.Add(cB.name, $"Liquidity fail: buyUSD={(cB.askPrice * cB.askAmount)}, sellUSD={(cS.bidPrice * cS.bidAmount)} (need >= {GlbConst.LiquidityCheckUsd})", LogType.Info);
                //Logger.Add(cB.name, $"Liquidity fail: buyUSD={(cB.askPrice * cB.askAmount):F2}, sellUSD={(cS.bidPrice * cS.bidAmount):F2} (need >= {GlbConst.LiquidityCheckUsd})", LogType.Info);
                return false;
            }

            if (cB.askPrice * cB.askAmount < cB.minOrderUSDT) { Logger.Add(cB.name, "cB_Amount < MinOrderUSDT", LogType.Info); return false; }
            if (cS.bidPrice * cS.bidAmount < cS.minOrderUSDT) { Logger.Add(cS.name, "cS_Amount < MinOrderUSDT", LogType.Info); return false; }

            double dIn = cS.bidPrice / cB.askPrice * 100 - 100;
            double dOut = cB.bidPrice / cS.askPrice * 100 - 100;

            double inNeed = InOutPercent.GetCurrentThresholds(cB, cS).inPrc;
            double outFloor = GlbConst.ExitSpreadFloorPercent;

            Logger.Add(cB.name, $"NowDeltaIn = {dIn:F3}%, NeedDeltaIn = {inNeed:F3}%; NowDeltaOut = {dOut:F3}%, MinDeltaOut = {outFloor:F3}%", LogType.Info);

            if (dIn < inNeed) return false;
            if (dOut < outFloor) return false;

            return true;
        }

        private static void LogPotentialDeal(CurData buy, CurData sell)
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var folder = Path.Combine(baseDir, "Deals");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, $"{buy.exchange}_{sell.exchange}.txt");
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            double deltaIn = (double)sell.bidPrice / (double)buy.askPrice * 100.0 - 100.0;
            double f1 = (double)buy.FundingRate;
            double f2 = (double)sell.FundingRate;

            var header = File.Exists(path) ? "" : "Timestamp,Coin,DeltaIn%,Funding_1%,Funding_2%\n";
            var line = $"{ts},{sell.name},{deltaIn:F3},{f1:F4},{f2:F4}\n";

            File.AppendAllText(path, header + line);
        }
    }
}
