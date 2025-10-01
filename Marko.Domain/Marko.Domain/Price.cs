using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marko.Domain
{
    public sealed class Price
    {
        public DateOnly Date { get; init; }
        public string Symbol { get; init; } = default!;
        public double PriceAdj { get; init; } 


    }
}
