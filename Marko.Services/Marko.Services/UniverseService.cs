using Marko.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marko.Services
{
    public interface IUniverseService
    {
        Task<List<string>> GetUniverseAsync(DateOnly date, CancellationToken ct);
    }

    public class UniverseService : IUniverseService
    {
        private readonly MarkoDbContext _db;
        public UniverseService(MarkoDbContext db) => _db = db;

        public Task<List<string>> GetUniverseAsync(DateOnly d, CancellationToken ct) =>
            _db.Memberships
               .Where(m => m.StartDate <= d && (m.EndDate == null || m.EndDate >= d))
               .Select(m => m.Symbol)
               .Distinct()
               .ToListAsync(ct);


    }
}
