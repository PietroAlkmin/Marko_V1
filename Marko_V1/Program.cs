using Marko.Data;
using Marko.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// registra fábrica do DbContext (thread-safe p/ singleton)
builder.Services.AddDbContextFactory<MarkoDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("Supabase")));

builder.Services.AddScoped<IUniverseService, UniverseService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
