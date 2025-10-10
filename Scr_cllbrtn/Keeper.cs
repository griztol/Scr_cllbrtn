using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scr_cllbrtn
{
    public class Keeper
    {
        DealOpener dealer = new DealOpener();

        public bool NeedNewDeals()
        {
            bool res = true;
            GlbConst.deals.RemoveAll(x => x.workDone);
            if (GlbConst.deals.Count >= GlbConst.MaxOpenedDeals) { res = false; }

            if (Directory.EnumerateFiles(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "stop_*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault() is string f)
            {
                GlbConst.workStopped = true;
                GlbConst.WaitingTime = int.Parse(Path.GetFileNameWithoutExtension(f).AsSpan(5));
                return false;
            }
            else 
            { 
                GlbConst.workStopped = false; 
            }

            return res;
        }

        public void Inspect(CurData curBuy, CurData curSell)
        {
            if (GlbConst.deals.Count >= GlbConst.MaxOpenedDeals) { return; }

            if (GlbConst.deals.Any(d => d.curBuy.name == curBuy.name || d.curSell.name == curBuy.name)) return;

            //double limitUsd = (double)AssetLimitManager.GetLimit(curSell.name) - (double)GlbConst.StepUsd * 0.5;
            //double limitUsd = 1.5;
            //if ((double)curSell.Balance * (double)curSell.askPrice > limitUsd ||
            //    (double)curBuy.Balance * (double)curBuy.bidPrice > limitUsd)
            //{
            //    Logger.Add(curSell.name, $"Balance > {limitUsd}$ (limit - step 50%)", LogType.Info);
            //    return;
            //}

            double deltaIn = (double)(curSell.bidPrice / curBuy.askPrice * 100 - 100);
            double deltaOut = (double)(curBuy.bidPrice / curSell.askPrice * 100 - 100);

            double inThreshold;
            try
            {
                if (curSell.InBlackList || curBuy.InBlackList)
                {
                    Logger.Add(curBuy.name, "In blacklist", LogType.Info);
                    return;
                }
                inThreshold = InOutPercent.GetCurrentThresholds(curBuy, curSell).inPrc;
            }
            catch (KeyNotFoundException e)      // meta[coin] not loaded yet
            {
                Logger.Add(curSell.name, "Meta not found → skip. " + e.Message, LogType.Error);
                return;                         // exit silently
            }

            if (deltaIn > inThreshold && deltaIn < 10)
            {
                DealCloser d = dealer.MakeDealAsync(curBuy, curSell).Result;
                if (d != null) { GlbConst.deals.Add(d); }
                Console.WriteLine($"{curBuy.name} {curBuy.exchange} {curSell.exchange} {deltaIn}");
            }

        }
    }

}
