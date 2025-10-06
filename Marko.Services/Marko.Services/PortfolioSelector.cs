using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Marko.Domain;
using Marko.Data;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Marko.Services
{
    public sealed class PortfolioSelector : IPortfolioSelector
    {
        private readonly MarkoDbContext _db;
        private readonly ILogger<PortfolioSelector>? _log;

        // coverage defaults (pode expor no SelectorConfig se quiser)
        private const double ColCoverageMin = 0.85; // % mínimo de datas válidas por ativo
        private const double RowCoverageMin = 0.80; // % mínimo de ativos válidos por data
        private const int MinRowsNeeded = 24;   // pelo menos 24 meses para cov

        public PortfolioSelector(MarkoDbContext db, ILogger<PortfolioSelector>? log = null)
        {
            _db = db;
            _log = log;
        }

        public async Task<PortfolioSelectionResult?> RunPeriodAsync(
            DateOnly start, DateOnly end, SelectorConfig cfg, CancellationToken ct = default)
        {
            // 1) datas do período
            var allDays = await _db.Prices
                .Where(p => p.Date >= start && p.Date <= end)
                .Select(p => p.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync(ct);
            if (allDays.Count == 0)
            {
                _log?.LogWarning("RunPeriod: sem dias no intervalo {Start}→{End}", start, end);
                return null;
            }

            var monthEnds = MonthEnds(allDays);
            var t0 = monthEnds.FirstOrDefault(d =>
            {
                var minLookbackDate = d.AddMonths(-cfg.lookbackMonths);
                return allDays.Any(x => x >= minLookbackDate && x < d);
            });
            if (t0 == default)
            {
                _log?.LogWarning("RunPeriod: não há mês com lookback completo (LB={LB}m).", cfg.lookbackMonths);
                return null;
            }

            // 2) universo elegível em t0
            var eligible = await _db.Memberships
                .Where(m => m.StartDate <= t0 && (m.EndDate == null || m.EndDate >= t0))
                .Select(m => m.Symbol)
                .Distinct()
                .ToListAsync(ct);
            if (eligible.Count == 0)
            {
                _log?.LogWarning("RunPeriod: nenhum símbolo elegível em {T0}", t0);
                return null;
            }

            // 3) preços do lookback
            var lbStart = t0.AddMonths(-cfg.lookbackMonths);
            var symbolsSet = eligible.ToHashSet(StringComparer.Ordinal);

            var pricesLb = await _db.Prices
                .Where(p => p.Date >= lbStart && p.Date <= t0 && symbolsSet.Contains(p.Symbol))
                .AsNoTracking()
                .ToListAsync(ct);
            if (pricesLb.Count == 0)
            {
                _log?.LogWarning("RunPeriod: sem preços no lookback {LBStart}→{T0}", lbStart, t0);
                return null;
            }

            var bySym = pricesLb
                .GroupBy(p => p.Symbol)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());

            var monthlyDates = MonthEnds(bySym.Values.SelectMany(v => v.Select(x => x.Date)).Distinct().OrderBy(d => d)).ToList();
            monthlyDates = monthlyDates.Where(d => d > lbStart && d <= t0).ToList();
            if (monthlyDates.Count < cfg.MinMonths)
            {
                _log?.LogWarning("RunPeriod: datas mensais insuficientes ({Have}<{Need}).", monthlyDates.Count, cfg.MinMonths);
                return null;
            }

            // 4) retornos mensais (nullable)
            var retsM = new Dictionary<string, List<double?>>(bySym.Count);
            foreach (var (sym, lst) in bySym)
            {
                var pxByDate = lst.ToDictionary(x => x.Date, x => (double)x.PriceAdj);
                var pxM = new List<double?>(monthlyDates.Count);
                foreach (var md in monthlyDates)
                    pxM.Add(pxByDate.TryGetValue(md, out var px) ? px : (double?)null);

                var r = ToReturns(pxM);
                if (r.Count >= cfg.MinMonths - 1 && r.Any(x => x.HasValue))
                    retsM[sym] = r;
            }
            if (retsM.Count == 0)
            {
                _log?.LogWarning("RunPeriod: nenhuma série de retorno mensal válida.");
                return null;
            }

            // 5) Top-N por Sharpe (usa só valores válidos)
            var rf_m = Math.Pow(1.0 + cfg.RiskFreeRate, 1.0 / 12.0) - 1.0;
            var sharpeRank = retsM
                .Select(kv => new { Sym = kv.Key, S = Sharpe(kv.Value, rf_m) })
                .Where(x => !double.IsNaN(x.S))
                .OrderByDescending(x => x.S)
                .Take(cfg.TopN)
                .ToList();
            if (sharpeRank.Count < cfg.KFinal)
            {
                _log?.LogWarning("RunPeriod: TopN ({Top}) < KFinal ({K}).", sharpeRank.Count, cfg.KFinal);
                return null;
            }

            var topSyms = sharpeRank.Select(x => x.Sym).ToList();

            // 6) Monta matriz R com tolerância a faltas (coverage por coluna/linha)
            var topCols = topSyms.Select(s => retsM[s]).ToList();
            var (R, keptRows, keptColsIdx) = BuildMatrixTolerant(topCols, ColCoverageMin, RowCoverageMin);
            if (R.RowCount < Math.Max(MinRowsNeeded, cfg.MinMonths - 1))
            {
                _log?.LogWarning("RunPeriod: T (linhas) insuficiente após filtro (T={T}).", R.RowCount);
                return null;
            }
            if (R.ColumnCount < cfg.KFinal)
            {
                _log?.LogWarning("RunPeriod: N (colunas) insuficiente após filtro (N={N}<K={K}).", R.ColumnCount, cfg.KFinal);
                return null;
            }

            // nomes das colunas mantidas
            var keptSyms = keptColsIdx.Select(i => topSyms[i]).ToList();

            var mu = MeanVector(R);
            var sigma = CovMatrix(R, cfg.Ridge);

            // 7) Markowitz heurístico
            var w0 = SolveHeuristic(mu, sigma, cfg.WMin, cfg.WMax, keptSyms);

            // 8) Poda para KFinal
            var wK = PruneToK(w0, cfg.KFinal, mu, sigma, cfg.WMin, cfg.WMax, keptSyms);

            // 9) Buy&Hold t0→end
            var datesFwd = await _db.Prices
                .Where(p => p.Date > t0 && p.Date <= end)
                .Select(p => p.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToListAsync(ct);
            if (datesFwd.Count == 0)
            {
                _log?.LogWarning("RunPeriod: sem preços no fwd {T0}→{End}.", t0, end);
                return null;
            }

            var fwdSyms = wK.Keys.ToHashSet(StringComparer.Ordinal);
            var pricesFwd = await _db.Prices
                .Where(p => p.Date >= t0 && p.Date <= end && fwdSyms.Contains(p.Symbol))
                .AsNoTracking()
                .ToListAsync(ct);

            var fwdBySym = pricesFwd
                .GroupBy(p => p.Symbol)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Date).ToList());

            var retDaily = ComputeDailyPortfolioReturns(fwdBySym, wK, t0, datesFwd);

            _log?.LogInformation("RunPeriod OK: t0={T0:d} | k={K} | primeiros: {Syms}",
                t0, wK.Count, string.Join(", ", wK.Keys.Take(10)));

            return new PortfolioSelectionResult
            {
                Date = t0,
                Symbols = wK.Keys.ToList(),
                Weights = wK,
                DailyReturns = retDaily
            };
        }

        // ---------------- Helpers ----------------

        private static List<DateOnly> MonthEnds(IEnumerable<DateOnly> dates) =>
            dates.GroupBy(d => (d.Year, d.Month)).Select(g => g.Max()).OrderBy(d => d).ToList();

        private static List<double?> ToReturns(List<double?> px)
        {
            var r = new List<double?>(Math.Max(0, px.Count - 1));
            for (int i = 1; i < px.Count; i++)
            {
                var p1 = px[i]; var p0 = px[i - 1];
                if (!p1.HasValue || !p0.HasValue || p0.Value == 0.0) { r.Add(null); continue; }
                r.Add(p1.Value / p0.Value - 1.0);
            }
            return r;
        }

        private static double Sharpe(List<double?> r, double rf_m)
        {
            var clean = r.Where(x => x.HasValue && !double.IsNaN(x.Value))
                         .Select(x => x!.Value).ToArray();
            if (clean.Length < 12) return double.NaN;
            var ex = clean.Select(x => x - rf_m).ToArray();
            var mean = ex.Average();
            var var_ = ex.Select(x => (x - mean) * (x - mean)).Sum() / Math.Max(1, ex.Length - 1);
            var sd = Math.Sqrt(Math.Max(var_, 0.0));
            if (sd <= 0) return double.NaN;
            return (mean / sd) * Math.Sqrt(12.0);
        }

        /// <summary>
        /// Constrói matriz T×N tolerante a NaNs:
        /// 1) começa com todas as linhas (T = comprimento da 1ª coluna).
        /// 2) filtra colunas com cobertura &lt; ColCoverageMin.
        /// 3) filtra linhas com ativos válidos &lt; RowCoverageMin * N.
        /// 4) demean por coluna (usando só válidos) e imputa NaN com 0 (pós-demean).
        /// </summary>
        private static (Matrix<double> M, List<int> keptRows, List<int> keptCols) BuildMatrixTolerant(
            List<List<double?>> cols, double colCoverageMin, double rowCoverageMin)
        {
            int T = cols[0].Count;
            int N0 = cols.Count;

            // 1) filtra colunas por cobertura
            var keepCols = new List<int>();
            for (int j = 0; j < N0; j++)
            {
                int valid = cols[j].Count(x => x.HasValue && !double.IsNaN(x.Value));
                double cov = (double)valid / T;
                if (cov >= colCoverageMin) keepCols.Add(j);
            }
            if (keepCols.Count == 0)
                return (Matrix<double>.Build.Dense(0, 0), new List<int>(), new List<int>());

            // extrai apenas colunas mantidas
            var C = keepCols.Select(j => cols[j]).ToList();
            int N = C.Count;

            // 2) filtra linhas por cobertura
            var keepRows = new List<int>();
            int minValidPerRow = Math.Max(1, (int)Math.Ceiling(rowCoverageMin * N));
            for (int i = 0; i < T; i++)
            {
                int v = 0;
                for (int j = 0; j < N; j++)
                {
                    var val = C[j][i];
                    if (val.HasValue && !double.IsNaN(val.Value)) v++;
                }
                if (v >= minValidPerRow) keepRows.Add(i);
            }
            if (keepRows.Count == 0)
                return (Matrix<double>.Build.Dense(0, 0), new List<int>(), new List<int>());

            // 3) constrói matriz com de-mean por coluna e imputação 0
            var M = Matrix<double>.Build.Dense(keepRows.Count, N);
            for (int j = 0; j < N; j++)
            {
                // média da coluna (apenas válidos nas linhas mantidas)
                var vals = new List<double>();
                foreach (var i in keepRows)
                    if (C[j][i].HasValue && !double.IsNaN(C[j][i]!.Value))
                        vals.Add(C[j][i]!.Value);

                double mean = (vals.Count > 0) ? vals.Average() : 0.0;

                for (int r = 0; r < keepRows.Count; r++)
                {
                    var i = keepRows[r];
                    var v = C[j][i];
                    if (v.HasValue && !double.IsNaN(v.Value))
                        M[r, j] = v.Value - mean; // demean
                    else
                        M[r, j] = 0.0;            // imputa 0 após demean
                }
            }

            return (M, keepRows, keepCols);
        }

        private static Vector<double> MeanVector(Matrix<double> R)
        {
            int N = R.ColumnCount;
            var mu = Vector<double>.Build.Dense(N);
            for (int j = 0; j < N; j++)
                mu[j] = R.Column(j).Average();
            return mu;
        }

        private static Matrix<double> CovMatrix(Matrix<double> R, double ridge)
        {
            int T = R.RowCount;
            var Rt = R.Transpose();
            var cov = (Rt * R) / Math.Max(1.0, (T - 1.0));

            // ridge relativo à mediana da diagonal
            var diag = Enumerable.Range(0, cov.RowCount).Select(i => cov[i, i]).OrderBy(x => x).ToArray();
            double med = diag.Length > 0 ? diag[diag.Length / 2] : 0.0;
            double lam = Math.Max(ridge, 0.05 * Math.Abs(med)); // 5% da mediana
            for (int i = 0; i < cov.RowCount; i++) cov[i, i] += lam;

            return cov;
        }

        private static Dictionary<string, double> SolveHeuristic(
            Vector<double> mu, Matrix<double> sigma, double wMin, double wMax, List<string> symbols)
        {
            Matrix<double> inv;
            try { inv = sigma.Inverse(); }
            catch
            {
                var s2 = sigma.Clone();
                for (int i = 0; i < s2.RowCount; i++) s2[i, i] += 0.10 * Math.Abs(s2[i, i]);
                inv = s2.Inverse();
            }

            var w = inv * mu;
            for (int i = 0; i < w.Count; i++) if (w[i] < 0) w[i] = 0;

            Normalize(w);
            ApplyBoundsAndRenorm(w, wMin, wMax);

            return symbols.Select((sym, i) => new { sym, weight = w[i] })
                          .ToDictionary(x => x.sym, x => x.weight);
        }

        private static Dictionary<string, double> PruneToK(
            Dictionary<string, double> w0, int k, Vector<double> mu, Matrix<double> sigma,
            double wMin, double wMax, List<string> symbols)
        {
            var w = Vector<double>.Build.Dense(w0.Count);
            foreach (var (kv, i) in w0.Select((kv, i) => (kv, i))) w[i] = kv.Value;

            var active = new HashSet<int>(Enumerable.Range(0, w.Count));
            while (active.Count > k)
            {
                int argmin = active.OrderBy(i => w[i]).First();
                active.Remove(argmin);

                var subIdx = active.OrderBy(i => i).ToArray();
                var muS = Vector<double>.Build.Dense(subIdx.Length);
                for (int j = 0; j < subIdx.Length; j++) muS[j] = mu[subIdx[j]];

                var sigS = Matrix<double>.Build.Dense(subIdx.Length, subIdx.Length);
                for (int r = 0; r < subIdx.Length; r++)
                    for (int c = 0; c < subIdx.Length; c++)
                        sigS[r, c] = sigma[subIdx[r], subIdx[c]];

                Matrix<double> inv;
                try { inv = sigS.Inverse(); }
                catch
                {
                    var s2 = sigS.Clone();
                    for (int i = 0; i < s2.RowCount; i++) s2[i, i] += 0.10 * Math.Abs(s2[i, i]);
                    inv = s2.Inverse();
                }

                var wS = inv * muS;
                for (int j = 0; j < wS.Count; j++) if (wS[j] < 0) wS[j] = 0;
                Normalize(wS);
                ApplyBoundsAndRenorm(wS, wMin, wMax);

                var wNew = Vector<double>.Build.Dense(w.Count);
                foreach (var (sub, j) in subIdx.Select((val, j) => (val, j)))
                    wNew[sub] = wS[j];
                w = wNew;
            }

            var final = new Dictionary<string, double>();
            foreach (var i in active.OrderBy(x => x))
                final[symbols[i]] = w[i];
            return final;
        }

        private static void Normalize(Vector<double> w)
        {
            var s = w.Sum();
            if (s <= 0) return;
            for (int i = 0; i < w.Count; i++) w[i] /= s;
        }

        private static void ApplyBoundsAndRenorm(Vector<double> w, double wMin, double wMax)
        {
            for (int iter = 0; iter < 10; iter++)
            {
                for (int i = 0; i < w.Count; i++)
                    w[i] = Math.Clamp(w[i], 0.0, wMax);

                Normalize(w);

                var need = new List<int>();
                double deficit = 0;
                for (int i = 0; i < w.Count; i++)
                {
                    if (w[i] < wMin)
                    {
                        deficit += (wMin - w[i]);
                        need.Add(i);
                        w[i] = wMin;
                    }
                }
                if (deficit > 0)
                {
                    var donors = Enumerable.Range(0, w.Count).Except(need).ToList();
                    var donorSum = donors.Sum(i => w[i] - wMin);
                    if (donorSum > 1e-9)
                    {
                        foreach (var i in donors)
                        {
                            var avail = (w[i] - wMin);
                            var take = deficit * (avail / donorSum);
                            w[i] -= take;
                        }
                    }
                    Normalize(w);
                }
            }
        }

        private static List<(DateOnly, double)> ComputeDailyPortfolioReturns(
            Dictionary<string, List<Price>> fwdBySym,
            IReadOnlyDictionary<string, double> wK,
            DateOnly t0, List<DateOnly> datesFwd,
            double clip = 0.35)
        {
            var idxPrice = new Dictionary<string, Dictionary<DateOnly, double>>();
            foreach (var (sym, lst) in fwdBySym)
                idxPrice[sym] = lst.ToDictionary(x => x.Date, x => (double)x.PriceAdj);

            var daily = new List<(DateOnly, double)>();
            for (int i = 1; i < datesFwd.Count; i++)
            {
                var d0 = datesFwd[i - 1];
                var d1 = datesFwd[i];

                var valid = new List<(double w, double r)>();
                foreach (var (sym, w) in wK)
                {
                    if (!idxPrice.TryGetValue(sym, out var px)) continue;
                    if (!px.TryGetValue(d0, out var p0) || !px.TryGetValue(d1, out var p1) || p0 == 0) continue;

                    var ri = p1 / p0 - 1.0;
                    if (ri > clip) ri = clip;
                    else if (ri < -clip) ri = -clip;
                    valid.Add((w, ri));
                }

                if (valid.Count == 0) { daily.Add((d1, 0.0)); continue; }

                double wsum = valid.Sum(v => v.w);
                if (wsum <= 0) { daily.Add((d1, 0.0)); continue; }

                double rday = 0.0;
                foreach (var v in valid) rday += (v.w / wsum) * v.r;
                daily.Add((d1, rday));
            }
            return daily;
        }
    }
}
