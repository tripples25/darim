using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Wishlist.Server.Data;
public class AppDbFactory:IDesignTimeDbContextFactory<AppDb>{public AppDb CreateDbContext(string[] args)=>new(new DbContextOptionsBuilder<AppDb>().UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Database")??"Host=localhost;Database=wishlist;Username=wishlist;Password=development-only").Options);}
