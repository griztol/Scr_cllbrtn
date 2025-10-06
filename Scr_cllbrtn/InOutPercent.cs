using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Scr_cllbrtn
{
    public static class InOutPercent
    {
        public static (double inPrc, double outPrc) GetCurrentThresholds(CurData buy, CurData sell)
        {
            return (1.5, 0);
        }

    }
}
