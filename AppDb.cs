using Microsoft.EntityFrameworkCore;

// El DbContext cumple el papel de la "Session" de SQLAlchemy.
public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<Producto> Productos => Set<Producto>();
    public DbSet<Pedido> Pedidos => Set<Pedido>();
    public DbSet<ItemPedido> Items => Set<ItemPedido>();
}
