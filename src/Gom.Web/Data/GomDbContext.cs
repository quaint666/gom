using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Data;

public class GomDbContext(DbContextOptions<GomDbContext> options) : DbContext(options);
