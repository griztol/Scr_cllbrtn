using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Screener
{
    public class CurData
    {
        public BaseExchange prnt;
        public decimal Balance { get { return prnt.GetBalance(this); } }
        public string name = "";
        public string exchange = "";
        public double askPrice;
        public double bidPrice;
        public double askAmount;
        public double bidAmount;
        public bool InBlackList => prnt.meta.TryGetValue(name, out var m) ? m.InBlackList : true;
        public double FundingRate => prnt.meta.TryGetValue(name, out var m) ? m.FundingRate : 0;
        //public double MinBuyUSDT => prnt.meta[name].MinBuyUSDT * 1.2;
        public double minOrderUSDT => prnt.meta[name].MinOrderUSDT * 1.2;
        public DateTime Timestamp;

        public CurData(BaseExchange prnt, string CurName)
        {
            this.prnt = prnt;
            this.name = CurName;
            exchange = prnt.exName;
    }

        public async Task<CurData> UpdateAsync()
        {
            return await prnt.GetLastPriceAsync(name);
        }
        
        public async Task<OrderResult> SellAsync(decimal vol, decimal price = 0, bool noAlign = false, bool fok = false)
        {
            return await prnt.SellAsync(name, vol, price, noAlign, fok);
        }

        public async Task<OrderResult> BuyAsync(decimal vol, decimal price = 0, bool noAlign = false, bool fok = false)
        {
            return await prnt.BuyAsync(name, vol, price, noAlign, fok);
        }

        public void AddToBlackList()
        {
            prnt.meta[name] = prnt.meta[name] with { InBlackList = true };
        }

        public decimal StepRound(decimal qty)
        {
            decimal step = prnt.meta[name].Step;
            decimal aligned = Math.Floor(Math.Abs(qty) / step) * step;
            return Math.Sign(qty) * aligned;
        }

    }
}
