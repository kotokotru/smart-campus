using Campus.Dormitory.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Campus.Dormitory.Api.Data;

public class DormitoryDbContext(DbContextOptions<DormitoryDbContext> options) : DbContext(options)
{
    public DbSet<DormitoryCheckIn> CheckIns => Set<DormitoryCheckIn>();
}
