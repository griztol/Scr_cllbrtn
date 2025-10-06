using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Screener
{
    public class OrderResult
    {
        public bool success = false;
        public string orderId = "";
        public string errMes = "";
        public string exName = "";
        public string curName = "";

        public OrderResult(string exName, string curName)
        {
            this.exName = exName;
            this.curName = curName;
        }
    }
}
