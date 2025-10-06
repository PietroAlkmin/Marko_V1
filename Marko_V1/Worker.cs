using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Marko.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marko_V1
{
    public sealed class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _log;
        private readonly IServiceScopeFactory _scopeFactory;

        public Worker(ILogger<Worker> log, IServiceScopeFactory scopeFactory)
        {
            _log = log;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stopToken)
        {
            // ---- 1) Config única para todos os períodos ----
            var cfg = new SelectorConfig
            {
                TopN = 100,
                KFinal = 45,
                WMin = 0.005,         // 0.5%
                WMax = 0.03,          // 3%
                RiskFreeRate = 0.04,  // anual
                lookbackMonths = 36,
                MinMonths = 24,
                Ridge = 1e-4
            };

            // ---- 2) Períodos a testar ----
            var periods = new (DateOnly start, DateOnly end, string name)[]
            {
                (new DateOnly(2010,01,01), new DateOnly(2013,01,01), "2010–2013"),
                (new DateOnly(2013,01,01), new DateOnly(2018,01,01), "2013–2018"),
                (new DateOnly(2018,01,01), new DateOnly(2023,01,01), "2018–2023"),
            };

            const double initialCapital = 100_000.0;

            // Acumuladores para o resumo final
            var allDaily = new List<double>();   // todos os retornos diários concatenados
            var perPeriodWealth = new List<(string Name, double Wealth)>(); // multiplicadores por período

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var selector = scope.ServiceProvider.GetRequiredService<IPortfolioSelector>();

                foreach (var (start, end, name) in periods)
                {
                    _log.LogInformation("=== Período {Name}: {Start:d} → {End:d} ===", name, start, end);

                    var res = await selector.RunPeriodAsync(start, end, cfg, stopToken);
                    if (res is null)
                    {
                        _log.LogWarning("Sem resultado (provável falta de dados).");
                        continue;
                    }

                    var daily = res.DailyReturns.Select(t => t.Ret).ToList();
                    var m = ComputeMetrics(daily, cfg.RiskFreeRate);

                    // guarda para o resumo
                    allDaily.AddRange(daily);
                    perPeriodWealth.Add((name, m.Wealth));

                    // prints do período
                    _log.LogInformation("t0={Date:d} | k={K} | primeiros: {Syms}",
                        res.Date, res.Symbols.Count, string.Join(", ", res.Symbols.Take(10)));
                    _log.LogInformation("CAGR {CAGR:P2} | Vol {Vol:P2} | Sharpe {Sharpe:F2} | MDD {MDD:P2} | Wealth x{Wealth:F2}",
                        m.CAGR, m.Vol, m.Sharpe, m.MDD, m.Wealth);

                    var periodReturnPct = (m.Wealth - 1.0) * 100.0;
                    var endValue = initialCapital * m.Wealth;
                    _log.LogInformation("💵 $100k → ${EndValue:N0}  ({Ret:+0.0;-0.0}%)", endValue, periodReturnPct);
                }

                // ===== RESUMO FINAL =====
                if (perPeriodWealth.Count > 0)
                {
                    // Wealth combinado (produto dos períodos)
                    double combinedWealth = perPeriodWealth.Select(x => x.Wealth).Aggregate(1.0, (acc, w) => acc * w);
                    double finalValue = initialCapital * combinedWealth;
                    double totalReturnPct = (combinedWealth - 1.0) * 100.0;

                    // Métricas sobre TODOS os dias concatenados (mais fiel)
                    var totalMetrics = ComputeMetrics(allDaily, rfAnnual: cfg.RiskFreeRate);

                    _log.LogInformation(new string('=', 80));
                    _log.LogInformation("🏁 RESUMO GERAL (3 períodos)");
                    foreach (var (name, w) in perPeriodWealth)
                        _log.LogInformation("  • {Name}: Wealth x{W:F2}  ({Ret:+0.0;-0.0}%)", name, w, (w - 1) * 100.0);

                    _log.LogInformation("");
                    _log.LogInformation("📊 MÉTRICAS CONSOLIDADAS (diárias concatenadas):");
                    _log.LogInformation("  • CAGR:   {CAGR:P2}", totalMetrics.CAGR);
                    _log.LogInformation("  • Vol:    {Vol:P2}", totalMetrics.Vol);
                    _log.LogInformation("  • Sharpe: {Sharpe:F2}", totalMetrics.Sharpe);
                    _log.LogInformation("  • MDD:    {MDD:P2}", totalMetrics.MDD);
                    _log.LogInformation("  • Wealth: x{Wealth:F2}", totalMetrics.Wealth);

                    _log.LogInformation("");
                    _log.LogInformation("💰 CAPITAL ($100.000 inicial):");
                    _log.LogInformation("  • Wealth combinado (produto): x{W:F2}", combinedWealth);
                    _log.LogInformation("  • Retorno total: {Ret:+0.0;-0.0}%", totalReturnPct);
                    _log.LogInformation("  • Valor final:   ${FV:N0}", finalValue);
                    _log.LogInformation(new string('=', 80));
                }
                else
                {
                    _log.LogWarning("Nenhum período retornou resultado. Nada a consolidar.");
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Erro ao executar Worker");
            }
        }

        // ---- métricas básicas (CAGR, Vol, Sharpe, MDD, Wealth) ----
        private static (double CAGR, double Vol, double Sharpe, double MDD, double Wealth)
            ComputeMetrics(IList<double> dailyRets, double rfAnnual, int ppy = 252)
        {
            if (dailyRets == null || dailyRets.Count == 0) return (0, 0, 0, 0, 1);

            // Wealth / CAGR
            double wealth = 1.0;
            foreach (var r in dailyRets) wealth *= (1.0 + r);
            var n = dailyRets.Count;
            var cagr = (n > 0 && wealth > 0) ? Math.Pow(wealth, (double)ppy / n) - 1.0 : 0.0;

            // Vol anual
            var mean = dailyRets.Average();
            var var_ = dailyRets.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, n - 1);
            var sd = Math.Sqrt(Math.Max(var_, 0.0));
            var volAnn = sd * Math.Sqrt(ppy);

            // Sharpe anual
            var rfDaily = Math.Pow(1.0 + rfAnnual, 1.0 / ppy) - 1.0;
            var ex = dailyRets.Select(x => x - rfDaily).ToList();
            var meanEx = ex.Average();
            var varEx = ex.Select(x => (x - meanEx) * (x - meanEx)).Sum() / Math.Max(1, n - 1);
            var sdEx = Math.Sqrt(Math.Max(varEx, 0.0));
            var sharpe = (sdEx > 0) ? (meanEx / sdEx) * Math.Sqrt(ppy) : 0.0;

            // Max Drawdown
            double peak = 1.0, eq = 1.0, mdd = 0.0;
            foreach (var r in dailyRets)
            {
                eq *= (1.0 + r);
                if (eq > peak) peak = eq;
                var dd = (eq / peak) - 1.0;
                if (dd < mdd) mdd = dd;
            }

            return (cagr, volAnn, sharpe, mdd, wealth);
        }
    }
}
