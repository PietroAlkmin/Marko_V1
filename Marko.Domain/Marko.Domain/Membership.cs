using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marko.Domain
{
    public sealed class Membership
    {
        public string Symbol { get; init; } = default!;
        public DateOnly StartDate { get; init; }
        public DateOnly? EndDate { get; init; }

    }
}
