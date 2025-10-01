//Esse arquivo serve para criar um serviço em segundo plano que interage com um banco de dados usando Entity Framework Core. Ele utiliza injeção de dependência para obter uma fábrica de contexto de banco de dados, permitindo a criação de contextos por escopo. O serviço consulta o banco de dados para contar o número de registros em duas tabelas e exibe uma amostra dos dados da tabela de cotacoes 
using Microsoft.EntityFrameworkCore;
using Marko.Data;

public sealed class Worker(
    ILogger<Worker> log,
    IDbContextFactory<MarkoDbContext> dbFactory   // << injeta a fábrica
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
