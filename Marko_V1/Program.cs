using Marko.Data;
using Marko.Services;
using Marko_V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Logging essencial
builder.Services.Configure<LoggerFilterOptions>(o =>
{
    o.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    o.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
});

// DbContext via Factory (Supabase + timeout) - PostgreSQL não tem EnableRetryOnFailure
builder.Services.AddDbContextFactory<MarkoDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Supabase"),
            npg => npg.CommandTimeout(180)));

// Resolve MarkoDbContext como Scoped a partir da fábrica
builder.Services.AddScoped<MarkoDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<MarkoDbContext>>().CreateDbContext());

// Serviços do domínio
builder.Services.AddScoped<IPortfolioSelector, PortfolioSelector>();

// Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
