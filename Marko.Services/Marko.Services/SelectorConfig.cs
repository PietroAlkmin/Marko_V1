using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marko.Services
{
    public sealed class SelectorConfig
    {
        public int lookbackMonths { get; set; } = 36; // Meses que o modelo considera para levar em conta a performance do ativo de acordo com o indice de sharpe
        public int MinMonths { get; set; } = 24; // meses minimos de dados para considerar o ativo
        public int TopN { get; set; } = 100; // Numero dos melhores n ativos (sharpe) que o modelo vai selecionar
        public int KFinal { get; set; } = 45; // Numero de ativos que a nossa carteira vai conter

        public double RiskFreeRate { get; set; } = 0.04; // Taxa livre de risco anual (Bonds do tesouro Americano)
        public double WMin { get; set; } = 0.005; // Peso minimo que um ativo pode ter na carteira
        public double WMax { get; set; } = 0.03; // Peso maximo que um ativo pode ter na carteira


        public double Ridge { get; set; } = 0.1; // Regularização simples da matriz de covariancia


    }
}
