using CarterCam.API.Services;
using System.Net;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ---- Encoder (must start FIRST to create pipe)
        builder.Services.AddSingleton<EncoderLauncher>(_ =>
            new EncoderLauncher(
                @"C:\Chase\ESP32 Projects\CarterCam\x64\Debug\CarterCam.Encoder.exe"
            )
        );

        // ---- Ingestion Engine
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

        // ---- Start Encoder first, then TCP server
        var encoder = app.Services.GetRequiredService<EncoderLauncher>();
        encoder.Start();
        
        // Give pipe time to initialize
        Thread.Sleep(500);

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
