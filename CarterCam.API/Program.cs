using CarterCam.API.Services;
using System.Net;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ---- Ingestion Engine (starts ONCE via constructor)
        builder.Services.AddSingleton<IngestionLauncher>(_ =>
            new IngestionLauncher(
                @"C:\Chase\ESP32 Projects\CarterCam\x64\Debug\CarterCam.Ingestor.exe"
            )
        );

        // ---- TCP Server
        builder.Services.AddSingleton<TCPServer>(sp =>
        {
            var ingestion = sp.GetRequiredService<IngestionLauncher>();
            return new TCPServer(5000, ingestion);
        });

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // ---- Start TCP server
        var tcpServer = app.Services.GetRequiredService<TCPServer>();
        Task.Run(() => tcpServer.Start());

        // ---- Resolve IPv4
        string ip = "unknown";
        foreach (var addr in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ip = addr.ToString();
                break;
            }
        }

        app.MapGet("/", () => $"CarterCam API is running! IP: {ip}");

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();

        app.Run("http://0.0.0.0:5157");
    }
}
