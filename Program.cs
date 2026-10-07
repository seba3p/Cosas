using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=tienda.db"));

// Los estados se envían/reciben como texto ("Pendiente") y no como número.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Acceso de administrador: una cookie de sesión que se obtiene con la contraseña.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "admin_tienda";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        // En una API devolvemos 401/403 en vez de redirigir a una página de login.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Crea el archivo tienda.db y las tablas la primera vez que se ejecuta.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();
}

// Sirve la interfaz desde la carpeta wwwroot (index.html).
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");

// ---------- ADMINISTRADOR ----------

// La contraseña está en appsettings.json -> "Admin": { "Password": "..." }
api.MapPost("/login", async (LoginRequest req, HttpContext http, IConfiguration config) =>
{
    var esperada = config["Admin:Password"] ?? "";
    var recibida = req.Password ?? "";

    // Comparación en tiempo constante (evita filtrar información por tiempos de respuesta).
    var correcta = esperada.Length > 0 && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(recibida), Encoding.UTF8.GetBytes(esperada));

    if (!correcta)
    {
        await Task.Delay(500);   // frena un poco los intentos de adivinar la contraseña
        return Results.BadRequest("Contraseña incorrecta.");
    }

    var identidad = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "admin") },
        CookieAuthenticationDefaults.AuthenticationScheme);

    await http.SignInAsync(new ClaimsPrincipal(identidad));
    return Results.Ok();
});

api.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync();
    return Results.Ok();
});

// Le dice a la interfaz si hay una sesión de administrador activa.
api.MapGet("/sesion", (HttpContext http) =>
    new { admin = http.User.Identity?.IsAuthenticated == true });

// ---------- PRODUCTOS ----------

api.MapGet("/productos", async (AppDb db) =>
    await db.Productos.OrderBy(p => p.Nombre).ToListAsync());

api.MapPost("/productos", async (Producto p, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(p.Nombre) || p.Precio < 0)
        return Results.BadRequest("Ingresá un nombre y un precio válido.");

    p.Id = 0;
    p.Nombre = p.Nombre.Trim();
    db.Productos.Add(p);
    await db.SaveChangesAsync();
    return Results.Created($"/api/productos/{p.Id}", p);
}).RequireAuthorization();

api.MapPut("/productos/{id:int}", async (int id, Producto datos, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(datos.Nombre) || datos.Precio < 0)
        return Results.BadRequest("Ingresá un nombre y un precio válido.");

    var p = await db.Productos.FindAsync(id);
    if (p is null) return Results.NotFound();

    p.Nombre = datos.Nombre.Trim();
    p.Precio = datos.Precio;
    await db.SaveChangesAsync();
    return Results.Ok(p);
}).RequireAuthorization();

api.MapDelete("/productos/{id:int}", async (int id, AppDb db) =>
{
    var p = await db.Productos.FindAsync(id);
    if (p is null) return Results.NotFound();

    db.Productos.Remove(p);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// ---------- PEDIDOS ----------

// GET /api/pedidos            -> todos
// GET /api/pedidos?estado=Pendiente -> filtrados
api.MapGet("/pedidos", async (EstadoPedido? estado, AppDb db) =>
{
    var consulta = db.Pedidos.Include(p => p.Items).AsQueryable();

    if (estado.HasValue)
        consulta = consulta.Where(p => p.Estado == estado.Value);

    return await consulta.OrderByDescending(p => p.Id).ToListAsync();
});

api.MapPost("/pedidos", async (NuevoPedido nuevo, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(nuevo.Cliente))
        return Results.BadRequest("El nombre del cliente es obligatorio.");

    if (nuevo.Items is null || nuevo.Items.Count == 0)
        return Results.BadRequest("El pedido debe tener al menos un producto.");

    if (nuevo.Items.Any(i => i.Cantidad <= 0))
        return Results.BadRequest("Las cantidades deben ser mayores a cero.");

    // Busca todos los productos de una vez y arma un diccionario {id: producto}.
    var ids = nuevo.Items.Select(i => i.ProductoId).Distinct().ToList();
    var productos = await db.Productos
        .Where(p => ids.Contains(p.Id))
        .ToDictionaryAsync(p => p.Id);

    var faltantes = ids.Where(id => !productos.ContainsKey(id)).ToList();
    if (faltantes.Count > 0)
        return Results.BadRequest($"Productos inexistentes: {string.Join(", ", faltantes)}");

    var pedido = new Pedido
    {
        Cliente = nuevo.Cliente.Trim(),
        Telefono = nuevo.Telefono?.Trim() ?? "",
        Direccion = string.IsNullOrWhiteSpace(nuevo.Direccion) ? null : nuevo.Direccion.Trim(),
        Notas = string.IsNullOrWhiteSpace(nuevo.Notas) ? null : nuevo.Notas.Trim(),
        // El precio SIEMPRE lo toma el servidor de la base de datos, no del cliente.
        Items = nuevo.Items.Select(i => new ItemPedido
        {
            ProductoId = i.ProductoId,
            NombreProducto = productos[i.ProductoId].Nombre,
            Cantidad = i.Cantidad,
            PrecioUnitario = productos[i.ProductoId].Precio
        }).ToList()
    };

    // Equivale a: sum(i.precio_unitario * i.cantidad for i in pedido.items)
    pedido.Total = pedido.Items.Sum(i => i.PrecioUnitario * i.Cantidad);

    db.Pedidos.Add(pedido);
    await db.SaveChangesAsync();
    return Results.Created($"/api/pedidos/{pedido.Id}", pedido);
});

api.MapPut("/pedidos/{id:int}/estado", async (int id, CambioEstado cambio, AppDb db) =>
{
    var pedido = await db.Pedidos.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
    if (pedido is null) return Results.NotFound();

    pedido.Estado = cambio.Estado;
    await db.SaveChangesAsync();
    return Results.Ok(pedido);
});

api.MapDelete("/pedidos/{id:int}", async (int id, AppDb db) =>
{
    var pedido = await db.Pedidos.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
    if (pedido is null) return Results.NotFound();

    db.Pedidos.Remove(pedido);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.Run();
