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

// DbContext via Factory (Supabase + timeout) - PostgreSQL n�o tem EnableRetryOnFailure
builder.Services.AddDbContextFactory<MarkoDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Supabase"),
            npg => npg.CommandTimeout(180)));

// Resolve MarkoDbContext como Scoped a partir da f�brica
builder.Services.AddScoped<MarkoDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<MarkoDbContext>>().CreateDbContext());

// Servi�os do dom�nio
builder.Services.AddScoped<IPortfolioSelector, PortfolioSelector>();

// Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
