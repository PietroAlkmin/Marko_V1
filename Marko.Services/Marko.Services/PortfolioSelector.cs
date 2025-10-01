using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Marko.Domain;
using Marko.Data;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.EntityFrameworkCore;

namespace Marko.Services
{
    public sealed class PortfolioSelector : IPortfolioSelector
    {
        private readonly MarkoDbContext _db;
        public PortfolioSelector(MarkoDbContext db) => _db = db;

        public async Task<PortfolioSelectionResult?> RunPeriodAsync(DateOnly start, DateOnly end, SelectorConfig cfg, CancellationToken ct = default)
        {
            // 1) t0: ultimo dia util do primeiro mes, permitindo lookback completo
        }


    }
}
