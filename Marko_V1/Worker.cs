//Esse arquivo serve para criar um servi�o em segundo plano que interage com um banco de dados usando Entity Framework Core. Ele utiliza inje��o de depend�ncia para obter uma f�brica de contexto de banco de dados, permitindo a cria��o de contextos por escopo. O servi�o consulta o banco de dados para contar o n�mero de registros em duas tabelas e exibe uma amostra dos dados da tabela de cotacoes 
using Microsoft.EntityFrameworkCore;
using Marko.Data;

public sealed class Worker(
    ILogger<Worker> log,
    IDbContextFactory<MarkoDbContext> dbFactory   // << injeta a f�brica
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await using var db = await dbFactory.CreateDbContextAsync(stop); // << cria contexto por escopo
        var totalPx = await db.Prices.CountAsync(stop);
        var totalMem = await db.Memberships.CountAsync(stop);
        log.LogInformation("rows: prices={px}, membership={mem}", totalPx, totalMem);

        var amostra = await db.Prices
            .Where(p => p.Symbol == "AAPL")
            .OrderBy(p => p.Date)
            .Take(5)
            .ToListAsync(stop);

        foreach (var r in amostra)
            log.LogInformation("{d} {s} {p}", r.Date, r.Symbol, r.PriceAdj);
    }
}
