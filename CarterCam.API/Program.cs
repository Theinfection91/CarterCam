using CarterCam.API.Services;
using System.Net;
using System.Net.WebSockets;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ---- Services
        builder.Services.AddSingleton<FrameBroadcaster>();
        
        builder.Services.AddSingleton<EncoderLauncher>(_ =>
            new EncoderLauncher(
                @"C:\Chase\ESP32 Projects\CarterCam\x64\Debug\CarterCam.Encoder.exe"
            )
        );

        builder.Services.AddSingleton<IngestionLauncher>(_ =>
            new IngestionLauncher(
                @"C:\Chase\ESP32 Projects\CarterCam\x64\Debug\CarterCam.Ingestor.exe"
            )
        );

        builder.Services.AddSingleton<TCPServer>(sp =>
        {
            var broadcaster = sp.GetRequiredService<FrameBroadcaster>();
            var ingestor = sp.GetRequiredService<IngestionLauncher>();
            return new TCPServer(5000, broadcaster, ingestor);
        });

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        var app = builder.Build();
        app.UseWebSockets();

        // ---- Start Encoder first (creates named pipe)
        var encoder = app.Services.GetRequiredService<EncoderLauncher>();
        encoder.Start();
        Thread.Sleep(500);

        // ---- Start TCP server
        var tcpServer = app.Services.GetRequiredService<TCPServer>();
        Task.Run(() => tcpServer.Start());

        var broadcaster = app.Services.GetRequiredService<FrameBroadcaster>();

        string ip = "unknown";
        foreach (var addr in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ip = addr.ToString();
                break;
            }
        }

        // ---- Recording API endpoints
        app.MapPost("/api/recording/start", () =>
        {
            tcpServer.StartRecording();
            return Results.Ok(new { recording = true, message = "Recording started" });
        });

        app.MapPost("/api/recording/stop", () =>
        {
            tcpServer.StopRecording();
            return Results.Ok(new { recording = false, message = "Recording stopped" });
        });

        app.MapGet("/api/recording/status", () =>
        {
            return Results.Ok(new { recording = tcpServer.IsRecording });
        });

        // ---- WebSocket endpoint for live stream
        app.Map("/ws/live", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                var clientId = Guid.NewGuid().ToString();
                
                broadcaster.AddClient(clientId, ws);

                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                    }
                    catch { break; }
                }

                broadcaster.RemoveClient(clientId);
                
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            else
            {
                context.Response.StatusCode = 400;
            }
        });

        // ---- Serve the live viewer HTML
        app.MapGet("/", () => Results.Content(GetViewerHtml(), "text/html"));

        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        app.UseAuthorization();
        app.MapControllers();

        Console.WriteLine($"Live stream: http://{ip}:5157/");
        app.Run("http://0.0.0.0:5157");
    }

    private static string GetViewerHtml() => """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="UTF-8">
            <title>CarterCam Live</title>
            <style>
                * { box-sizing: border-box; }
                body {
                    background: #1a1a1a;
                    color: white;
                    font-family: Arial, sans-serif;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    padding: 20px;
                    margin: 0;
                }
                h1 { margin-bottom: 10px; }
                .status-bar {
                    display: flex;
                    gap: 15px;
                    margin-bottom: 15px;
                    align-items: center;
                }
                .status {
                    padding: 5px 15px;
                    border-radius: 20px;
                    font-size: 14px;
                    font-weight: bold;
                }
                .connected { background: #2e7d32; }
                .disconnected { background: #c62828; }
                .connecting { background: #f57c00; }
                .recording { background: #c62828; animation: pulse 1s infinite; }
                .not-recording { background: #424242; }
                @keyframes pulse {
                    0%, 100% { opacity: 1; }
                    50% { opacity: 0.6; }
                }
                #stream {
                    border: 2px solid #333;
                    border-radius: 8px;
                    max-width: 90vw;
                    background: #000;
                }
                .controls {
                    margin-top: 15px;
                    display: flex;
                    gap: 10px;
                }
                button {
                    padding: 12px 24px;
                    font-size: 16px;
                    border: none;
                    border-radius: 8px;
                    cursor: pointer;
                    transition: all 0.2s;
                    font-weight: bold;
                }
                button:hover { transform: scale(1.05); }
                button:disabled { opacity: 0.5; cursor: not-allowed; transform: none; }
                #startBtn { background: #c62828; color: white; }
                #stopBtn { background: #424242; color: white; }
                .stats {
                    margin-top: 15px;
                    font-size: 14px;
                    color: #888;
                    display: flex;
                    gap: 20px;
                }
            </style>
        </head>
        <body>
            <h1>CarterCam Live</h1>
            
            <div class="status-bar">
                <div id="connStatus" class="status connecting">Connecting...</div>
                <div id="recStatus" class="status not-recording">NOT RECORDING</div>
            </div>
            
            <img id="stream" width="640" height="480" alt="Live Stream">
            
            <div class="controls">
                <button id="startBtn" onclick="startRecording()">START RECORDING</button>
                <button id="stopBtn" onclick="stopRecording()" disabled>STOP RECORDING</button>
            </div>
            
            <div class="stats">
                <span id="fps">FPS: --</span>
                <span id="frames">Frames: 0</span>
            </div>
            
            <script>
                const img = document.getElementById('stream');
                const connStatus = document.getElementById('connStatus');
                const recStatus = document.getElementById('recStatus');
                const fpsDisplay = document.getElementById('fps');
                const framesDisplay = document.getElementById('frames');
                const startBtn = document.getElementById('startBtn');
                const stopBtn = document.getElementById('stopBtn');
                
                let frameCount = 0;
                let totalFrames = 0;
                let lastTime = performance.now();
                let ws;
                let isRecording = false;
                
                function connect() {
                    connStatus.textContent = 'Connecting...';
                    connStatus.className = 'status connecting';
                    
                    ws = new WebSocket('ws://' + window.location.host + '/ws/live');
                    ws.binaryType = 'arraybuffer';
                    
                    ws.onopen = function() {
                        connStatus.textContent = 'LIVE';
                        connStatus.className = 'status connected';
                        checkRecordingStatus();
                    };
                    
                    ws.onmessage = function(event) {
                        const blob = new Blob([event.data], { type: 'image/jpeg' });
                        const url = URL.createObjectURL(blob);
                        img.onload = function() { URL.revokeObjectURL(url); };
                        img.src = url;
                        
                        frameCount++;
                        totalFrames++;
                        const now = performance.now();
                        if (now - lastTime >= 1000) {
                            fpsDisplay.textContent = 'FPS: ' + frameCount;
                            frameCount = 0;
                            lastTime = now;
                        }
                        framesDisplay.textContent = 'Frames: ' + totalFrames;
                    };
                    
                    ws.onclose = function() {
                        connStatus.textContent = 'DISCONNECTED';
                        connStatus.className = 'status disconnected';
                        setTimeout(connect, 2000);
                    };
                    
                    ws.onerror = function() { ws.close(); };
                }
                
                function startRecording() {
                    startBtn.disabled = true;
                    fetch('/api/recording/start', { method: 'POST' })
                        .then(function(res) { return res.json(); })
                        .then(function(data) {
                            if (data.recording) {
                                isRecording = true;
                                updateRecordingUI();
                            }
                            startBtn.disabled = false;
                        })
                        .catch(function(e) {
                            console.error('Failed to start recording:', e);
                            startBtn.disabled = false;
                        });
                }
                
                function stopRecording() {
                    stopBtn.disabled = true;
                    fetch('/api/recording/stop', { method: 'POST' })
                        .then(function(res) { return res.json(); })
                        .then(function(data) {
                            if (!data.recording) {
                                isRecording = false;
                                updateRecordingUI();
                            }
                            stopBtn.disabled = false;
                        })
                        .catch(function(e) {
                            console.error('Failed to stop recording:', e);
                            stopBtn.disabled = false;
                        });
                }
                
                function checkRecordingStatus() {
                    fetch('/api/recording/status')
                        .then(function(res) { return res.json(); })
                        .then(function(data) {
                            isRecording = data.recording;
                            updateRecordingUI();
                        })
                        .catch(function(e) {
                            console.error('Failed to check recording status:', e);
                        });
                }
                
                function updateRecordingUI() {
                    if (isRecording) {
                        recStatus.textContent = 'RECORDING';
                        recStatus.className = 'status recording';
                        startBtn.disabled = true;
                        stopBtn.disabled = false;
                    } else {
                        recStatus.textContent = 'NOT RECORDING';
                        recStatus.className = 'status not-recording';
                        startBtn.disabled = false;
                        stopBtn.disabled = true;
                    }
                }
                
                setInterval(checkRecordingStatus, 5000);
                connect();
            </script>
        </body>
        </html>
        """;
}
