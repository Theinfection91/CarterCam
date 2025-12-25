using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Get local IPv4
string ip = "unknown";
var hostName = Dns.GetHostName();
var hostEntry = Dns.GetHostEntry(hostName);
foreach (var addr in hostEntry.AddressList)
{
    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    {
        ip = addr.ToString();
        break;
    }
}

app.MapGet("/", () => $"CarterCam API is running! IP: {ip}");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run("http://0.0.0.0:5157");
