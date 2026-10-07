// Modelos = como las clases de SQLAlchemy en Python (cada clase es una tabla).

public enum EstadoPedido
{
    Pendiente,
    Preparando,
    EnCamino,
    Entregado,
    Cancelado
}

public class Producto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
}

public class Pedido
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; } = DateTime.UtcNow;
    public string Cliente { get; set; } = "";
    public string Telefono { get; set; } = "";
    public string? Direccion { get; set; }   // "?" = puede ser null (como Optional[str])
    public string? Notas { get; set; }
    public EstadoPedido Estado { get; set; } = EstadoPedido.Pendiente;
    public decimal Total { get; set; }
    public List<ItemPedido> Items { get; set; } = new();
}

public class ItemPedido
{
    public int Id { get; set; }
    public int PedidoId { get; set; }
    public int ProductoId { get; set; }
    public string NombreProducto { get; set; } = "";   // copia del nombre al momento del pedido
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }        // copia del precio al momento del pedido
}

// DTOs: lo que recibe la API al crear/actualizar (como los modelos Pydantic de FastAPI).
public record NuevoItem(int ProductoId, int Cantidad);

public record NuevoPedido(
    string Cliente,
    string? Telefono,
    string? Direccion,
    string? Notas,
    List<NuevoItem> Items);

public record CambioEstado(EstadoPedido Estado);

public record LoginRequest(string? Password);
