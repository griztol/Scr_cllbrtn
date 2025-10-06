using Screener.Exchanges;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screener
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

            //LogPotentialDeal(curBuy, curSell);
            //return null;

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
            bool res = true;
            if (cS == null || cB == null) { return false; }

            double dIn = (double)(cS.bidPrice / cB.askPrice * 100 - 100);
            if (cB.askPrice * cB.askAmount < cB.minOrderUSDT) { Logger.Add(cB.name,"cB_Amount " + cB.askPrice * cB.askAmount + " < MinBuyUSDT", LogType.Info); return false; }
            if (cS.bidPrice * cS.bidAmount < cS.minOrderUSDT) { Logger.Add(cS.name, "cS_Amount " + cS.bidPrice * cS.bidAmount + " < MinBuyUSDT", LogType.Info); return false; }

            Logger.Add(cB.name, "NowDelta = " + dIn + ", NeedDelta = " + InOutPercent.GetCurrentThresholds(cB, cS).inPrc, LogType.Info);
            if (dIn < InOutPercent.GetCurrentThresholds(cB, cS).inPrc) { return false; }

            return res;
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
