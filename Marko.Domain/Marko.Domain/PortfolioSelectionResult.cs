using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marko.Domain
{
    public sealed class PortfolioSelectionResult
    {
        public DateOnly Date { get; init; } // Momento da seleção da carteira, a data de compra 
        public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>(); // Lista dos k_finais ativos da carteira e a interface IReadOnlyList garante que a lista não possa ser modificada após a inicialização
        public IReadOnlyDictionary<string, double> Weights { get; init; } = new Dictionary<string, double>(); // O Peso de cada ativo na carteira, com a mesma logica de imutabilidade

        public IReadOnlyList<(DateOnly Date, double Ret)> DailyReturns { get; init; } = new List<(DateOnly, double)>(); // Retornos diários da carteira, representados como uma lista de tuplas contendo a data e o retorno. Basicamente o retorno da carteira no dia

    }
}
