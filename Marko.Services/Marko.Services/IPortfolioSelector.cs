using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Marko.Domain;

namespace Marko.Services
{
    public interface IPortfolioSelector
    {
        // Basicamente representa o retorno da carteira
        Task<PortfolioSelectionResult?> RunPeriodAsync(DateOnly start, DateOnly end, SelectorConfig cfg, CancellationToken ct = default);
    }
}
