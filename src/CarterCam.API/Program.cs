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
            var ingestor    = sp.GetRequiredService<IngestionLauncher>();
            return new TCPServer(5000, broadcaster, ingestor);
        });

        builder.Services.AddControllers();
        builder.Services.AddOpenApi();

        var app = builder.Build();
        app.UseWebSockets();

        var encoder = app.Services.GetRequiredService<EncoderLauncher>();
        encoder.Start();
        Thread.Sleep(500);

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
            Results.Ok(new { recording = tcpServer.IsRecording }));

        app.MapPost("/api/motor/left",        () => { tcpServer.SendMotorCommand('L'); return Results.Ok(new { message = "Motor moved left" }); });
        app.MapPost("/api/motor/center",      () => { tcpServer.SendMotorCommand('C'); return Results.Ok(new { message = "Motor centered" }); });
        app.MapPost("/api/motor/right",       () => { tcpServer.SendMotorCommand('R'); return Results.Ok(new { message = "Motor moved right" }); });
        app.MapPost("/api/motor/sweep/start", () => { tcpServer.SendMotorCommand('S'); return Results.Ok(new { message = "Motor sweep started" }); });
        app.MapPost("/api/motor/sweep/stop",  () => { tcpServer.SendMotorCommand('X'); return Results.Ok(new { message = "Motor sweep stopped" }); });

        app.Map("/ws/live", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var ws       = await context.WebSockets.AcceptWebSocketAsync();
                var clientId = Guid.NewGuid().ToString();
                broadcaster.AddClient(clientId, ws);

                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
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
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

                body {
                    background: #111;
                    color: #e0e0e0;
                    font-family: 'Segoe UI', Arial, sans-serif;
                    min-height: 100vh;
                    display: flex;
                    flex-direction: column;
                    align-items: center;
                    padding: 18px 20px 30px;
                    gap: 14px;
                }

                /* ---- Header ---- */
                .header {
                    display: flex;
                    align-items: center;
                    gap: 14px;
                    width: 100%;
                    max-width: 1100px;
                    justify-content: space-between;
                }
                h1 {
                    font-size: 22px;
                    letter-spacing: 3px;
                    text-transform: uppercase;
                    background: linear-gradient(90deg, #e53935, #ff7043, #ffd54f);
                    -webkit-background-clip: text;
                    -webkit-text-fill-color: transparent;
                    background-clip: text;
                }
                .status-bar { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
                .status {
                    padding: 4px 13px;
                    border-radius: 20px;
                    font-size: 12px;
                    font-weight: bold;
                    letter-spacing: 0.5px;
                }
                .connected     { background: #1b5e20; color: #a5d6a7; border: 1px solid #2e7d32; }
                .disconnected  { background: #b71c1c; color: #ef9a9a; border: 1px solid #c62828; }
                .connecting    { background: #e65100; color: #ffe0b2; border: 1px solid #f57c00; }
                .recording     { background: #b71c1c; color: #fff; border: 1px solid #e53935; animation: pulse 1s infinite; }
                .not-recording { background: #212121; color: #757575; border: 1px solid #333; }
                .uptime-badge  { background: #1a237e; color: #90caf9; border: 1px solid #283593;
                                 padding: 4px 13px; border-radius: 20px; font-size: 12px; font-weight: bold; }

                @keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.55; } }

                /* ---- Main layout ---- */
                .main-layout {
                    display: flex;
                    gap: 16px;
                    width: 100%;
                    max-width: 1100px;
                    align-items: flex-start;
                }

                /* ---- Stream column ---- */
                .stream-col { display: flex; flex-direction: column; gap: 10px; flex: 0 0 auto; }
                .stream-wrap {
                    position: relative;
                    border-radius: 10px;
                    overflow: hidden;
                    border: 2px solid #2a2a2a;
                    transition: border-color 0.3s, box-shadow 0.3s;
                    background: #000;
                    line-height: 0;
                }
                .stream-wrap.recording-active {
                    border-color: #e53935;
                    box-shadow: 0 0 18px #e5393588;
                }
                #stream { display: block; max-width: 100%; border-radius: 8px; }

                /* Scanline + vignette overlay */
                .stream-overlay {
                    position: absolute; inset: 0; pointer-events: none; border-radius: 8px;
                    background:
                        repeating-linear-gradient(
                            0deg,
                            transparent,
                            transparent 2px,
                            rgba(0,0,0,0.07) 2px,
                            rgba(0,0,0,0.07) 4px
                        ),
                        radial-gradient(ellipse at center, transparent 60%, rgba(0,0,0,0.55) 100%);
                }

                /* REC dot */
                .rec-indicator {
                    position: absolute;
                    top: 10px; left: 12px;
                    display: flex; align-items: center; gap: 6px;
                    font-size: 11px; font-weight: bold; letter-spacing: 1px;
                    color: #fff; opacity: 0; transition: opacity 0.3s;
                    text-shadow: 0 0 6px #000;
                }
                .rec-indicator.visible { opacity: 1; }
                .rec-dot {
                    width: 9px; height: 9px; border-radius: 50%;
                    background: #e53935; animation: pulse 1s infinite;
                }

                /* Keyboard hint */
                .kbd-hint {
                    font-size: 11px;
                    color: #444;
                    text-align: center;
                    letter-spacing: 0.3px;
                }
                .kbd-hint kbd {
                    background: #222; border: 1px solid #444; border-radius: 4px;
                    padding: 1px 5px; font-size: 11px; color: #aaa;
                }

                /* Stats */
                .stats {
                    display: flex; gap: 16px;
                    font-size: 12px; color: #555;
                    justify-content: center;
                }

                /* ---- Controls column ---- */
                .controls-col {
                    display: flex;
                    flex-direction: column;
                    gap: 12px;
                    flex: 1 1 0;
                    min-width: 260px;
                }

                /* ---- Panel ---- */
                .panel {
                    background: #181818;
                    border: 1px solid #2a2a2a;
                    border-radius: 10px;
                    padding: 13px 16px 15px;
                }
                .panel-title {
                    font-size: 10px;
                    font-weight: bold;
                    letter-spacing: 2px;
                    color: #555;
                    text-transform: uppercase;
                    margin-bottom: 11px;
                    padding-bottom: 8px;
                    border-bottom: 1px solid #242424;
                }
                .btn-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }

                /* ---- Buttons ---- */
                button {
                    padding: 9px 16px;
                    font-size: 13px;
                    font-weight: bold;
                    border: none;
                    border-radius: 7px;
                    cursor: pointer;
                    transition: filter 0.12s, transform 0.1s, box-shadow 0.12s;
                    color: #fff;
                    letter-spacing: 0.3px;
                }
                button:hover   { filter: brightness(1.2); transform: translateY(-1px); }
                button:active  { transform: translateY(1px) scale(0.97); filter: brightness(0.9); }
                button:disabled { opacity: 0.35; cursor: not-allowed; transform: none; filter: none; }

                #startBtn    { background: #c62828; }
                #stopBtn     { background: #3a3a3a; }
                #snapshotBtn { background: #1565c0; }

                .btn-motor { background: #1565c0; }
                .btn-sweep { background: #6a1b9a; }

                .btn-listen { background: #00695c; }
                .btn-talk   { background: #bf360c; }
                .btn-listen.active { background: #00897b; box-shadow: 0 0 10px #00bfa566; }
                .btn-talk.active   { background: #e64a19; box-shadow: 0 0 10px #ff6d0066; animation: pulse 1s infinite; }

                .audio-note { font-size: 10px; color: #444; margin-top: 9px; font-style: italic; }

                /* ---- Toast notifications ---- */
                #toast-container {
                    position: fixed; bottom: 24px; right: 24px;
                    display: flex; flex-direction: column; gap: 8px;
                    z-index: 9999; pointer-events: none;
                }
                .toast {
                    background: #1e1e1e;
                    border: 1px solid #333;
                    border-left: 3px solid #4caf50;
                    color: #e0e0e0;
                    padding: 10px 16px;
                    border-radius: 7px;
                    font-size: 13px;
                    min-width: 200px;
                    box-shadow: 0 4px 16px #00000088;
                    animation: toastIn 0.2s ease, toastOut 0.3s ease 2.7s forwards;
                    pointer-events: none;
                }
                .toast.warn  { border-left-color: #f57c00; }
                .toast.error { border-left-color: #e53935; }
                @keyframes toastIn  { from { opacity:0; transform:translateY(10px); } to { opacity:1; transform:translateY(0); } }
                @keyframes toastOut { from { opacity:1; } to { opacity:0; transform:translateY(6px); } }

                /* ---- Responsive ---- */
                @media (max-width: 780px) {
                    .main-layout { flex-direction: column; align-items: center; }
                    .stream-col, .controls-col { width: 100%; max-width: 500px; }
                }
            </style>
        </head>
        <body>

            <!-- Header -->
            <div class="header">
                <h1>&#128247; CarterCam</h1>
                <div class="status-bar">
                    <div id="connStatus"  class="status connecting">Connecting...</div>
                    <div id="recStatus"   class="status not-recording">NOT RECORDING</div>
                    <div id="audioStatus" class="status" style="background:#1a1a1a;color:#444;border:1px solid #2a2a2a;">AUDIO OFF</div>
                    <div class="uptime-badge">&#9203; <span id="uptime">00:00:00</span></div>
                </div>
            </div>

            <!-- Main layout -->
            <div class="main-layout">

                <!-- Stream column -->
                <div class="stream-col">
                    <div class="stream-wrap" id="streamWrap">
                        <img id="stream" width="640" height="480" alt="Live Stream">
                        <div class="stream-overlay"></div>
                        <div class="rec-indicator" id="recIndicator">
                            <div class="rec-dot"></div> REC
                        </div>
                    </div>
                    <div class="stats">
                        <span id="fps">FPS: --</span>
                        <span id="frames">Frames: 0</span>
                    </div>
                    <div class="kbd-hint">
                        Motor shortcuts: <kbd>&#8592;</kbd> <kbd>C</kbd> <kbd>&#8594;</kbd> &nbsp;|&nbsp;
                        Sweep: <kbd>S</kbd> start &nbsp; <kbd>X</kbd> stop
                    </div>
                </div>

                <!-- Controls column -->
                <div class="controls-col">

                    <!-- Recording -->
                    <div class="panel">
                        <div class="panel-title">&#9210; Recording</div>
                        <div class="btn-row">
                            <button id="startBtn"    onclick="startRecording()">&#9679; START</button>
                            <button id="stopBtn"     onclick="stopRecording()" disabled>&#9632; STOP</button>
                            <button id="snapshotBtn" onclick="takeSnapshot()">&#128247; SNAPSHOT</button>
                        </div>
                    </div>

                    <!-- Motor -->
                    <div class="panel">
                        <div class="panel-title">&#9881; Motor / Pan</div>
                        <div class="btn-row">
                            <button class="btn-motor" onclick="motorLeft()"   title="Arrow Left">&#9668; LEFT</button>
                            <button class="btn-motor" onclick="motorCenter()" title="C">&#9632; CENTER</button>
                            <button class="btn-motor" onclick="motorRight()"  title="Arrow Right">RIGHT &#9658;</button>
                        </div>
                        <div class="btn-row" style="margin-top:8px;">
                            <button class="btn-sweep" onclick="startSweep()" title="S">&#8635; START SWEEP</button>
                            <button class="btn-sweep" onclick="stopSweep()"  title="X">&#9726; STOP SWEEP</button>
                        </div>
                    </div>

                    <!-- Audio -->
                    <div class="panel">
                        <div class="panel-title">&#127908; Audio</div>
                        <div class="btn-row">
                            <button id="listenBtn" class="btn-listen" onclick="toggleListen()">&#128266; LISTEN</button>
                            <button id="talkBtn"   class="btn-talk"   onclick="toggleTalk()">&#127908; TALK</button>
                        </div>
                        <div class="audio-note">Audio streaming not yet implemented &mdash; placeholders only.</div>
                    </div>

                </div>
            </div>

            <!-- Toast container -->
            <div id="toast-container"></div>

            <script>
                // ---- Element refs ----
                const img          = document.getElementById('stream');
                const streamWrap   = document.getElementById('streamWrap');
                const connStatus   = document.getElementById('connStatus');
                const recStatus    = document.getElementById('recStatus');
                const audioStatus  = document.getElementById('audioStatus');
                const recIndicator = document.getElementById('recIndicator');
                const startBtn     = document.getElementById('startBtn');
                const stopBtn      = document.getElementById('stopBtn');
                const listenBtn    = document.getElementById('listenBtn');
                const talkBtn      = document.getElementById('talkBtn');

                let frameCount  = 0;
                let totalFrames = 0;
                let lastTime    = performance.now();
                let sessionStart = Date.now();
                let ws;
                let isRecording = false;
                let isListening = false;
                let isTalking   = false;

                // ---- Uptime timer ----
                setInterval(() => {
                    const s = Math.floor((Date.now() - sessionStart) / 1000);
                    const h = String(Math.floor(s / 3600)).padStart(2,'0');
                    const m = String(Math.floor((s % 3600) / 60)).padStart(2,'0');
                    const sec = String(s % 60).padStart(2,'0');
                    document.getElementById('uptime').textContent = h + ':' + m + ':' + sec;
                }, 1000);

                // ---- Toast ----
                function toast(msg, type = 'info') {
                    const el = document.createElement('div');
                    el.className = 'toast' + (type === 'warn' ? ' warn' : type === 'error' ? ' error' : '');
                    el.textContent = msg;
                    const container = document.getElementById('toast-container');
                    container.appendChild(el);
                    setTimeout(() => el.remove(), 3100);
                }

                // ---- WebSocket / stream ----
                function connect() {
                    connStatus.textContent = 'Connecting...';
                    connStatus.className = 'status connecting';
                    const protocol = window.location.protocol === 'https:' ? 'wss://' : 'ws://';
                    ws = new WebSocket(protocol + window.location.host + '/ws/live');
                    ws.binaryType = 'arraybuffer';

                    ws.onopen = () => {
                        connStatus.textContent = 'LIVE';
                        connStatus.className = 'status connected';
                        sessionStart = Date.now();
                        checkRecordingStatus();
                        toast('Connected to CarterCam');
                    };

                    ws.onmessage = (event) => {
                        const blob = new Blob([event.data], { type: 'image/jpeg' });
                        const url  = URL.createObjectURL(blob);
                        img.onload = () => URL.revokeObjectURL(url);
                        img.src = url;

                        frameCount++;
                        totalFrames++;
                        const now = performance.now();
                        if (now - lastTime >= 1000) {
                            document.getElementById('fps').textContent = 'FPS: ' + frameCount;
                            frameCount = 0;
                            lastTime = now;
                        }
                        document.getElementById('frames').textContent = 'Frames: ' + totalFrames;
                    };

                    ws.onclose = () => {
                        connStatus.textContent = 'DISCONNECTED';
                        connStatus.className = 'status disconnected';
                        toast('Connection lost — reconnecting...', 'warn');
                        setTimeout(connect, 2000);
                    };

                    ws.onerror = () => ws.close();
                }

                // ---- Snapshot ----
                function takeSnapshot() {
                    if (!img.src || img.src === window.location.href) { toast('No frame to capture', 'warn'); return; }
                    const canvas = document.createElement('canvas');
                    canvas.width  = img.naturalWidth  || img.width;
                    canvas.height = img.naturalHeight || img.height;
                    canvas.getContext('2d').drawImage(img, 0, 0);
                    const a = document.createElement('a');
                    a.download = 'cartercam-' + new Date().toISOString().replace(/[:.]/g,'-') + '.png';
                    a.href = canvas.toDataURL('image/png');
                    a.click();
                    toast('Snapshot saved');
                }

                // ---- Recording ----
                function startRecording() {
                    startBtn.disabled = true;
                    fetch('/api/recording/start', { method: 'POST' })
                        .then(r => r.json())
                        .then(d => {
                            if (d.recording) { isRecording = true; updateRecordingUI(); toast('Recording started', 'warn'); }
                            startBtn.disabled = false;
                        })
                        .catch(() => { toast('Failed to start recording', 'error'); startBtn.disabled = false; });
                }

                function stopRecording() {
                    stopBtn.disabled = true;
                    fetch('/api/recording/stop', { method: 'POST' })
                        .then(r => r.json())
                        .then(d => {
                            if (!d.recording) { isRecording = false; updateRecordingUI(); toast('Recording stopped'); }
                            stopBtn.disabled = false;
                        })
                        .catch(() => { toast('Failed to stop recording', 'error'); stopBtn.disabled = false; });
                }

                function checkRecordingStatus() {
                    fetch('/api/recording/status')
                        .then(r => r.json())
                        .then(d => { isRecording = d.recording; updateRecordingUI(); })
                        .catch(() => {});
                }

                function updateRecordingUI() {
                    if (isRecording) {
                        recStatus.textContent = 'RECORDING';
                        recStatus.className   = 'status recording';
                        streamWrap.classList.add('recording-active');
                        recIndicator.classList.add('visible');
                        startBtn.disabled = true;
                        stopBtn.disabled  = false;
                    } else {
                        recStatus.textContent = 'NOT RECORDING';
                        recStatus.className   = 'status not-recording';
                        streamWrap.classList.remove('recording-active');
                        recIndicator.classList.remove('visible');
                        startBtn.disabled = false;
                        stopBtn.disabled  = true;
                    }
                }

                // ---- Motor ----
                function motorLeft()   { fetch('/api/motor/left',        { method: 'POST' }); toast('← Left'); }
                function motorCenter() { fetch('/api/motor/center',      { method: 'POST' }); toast('Center'); }
                function motorRight()  { fetch('/api/motor/right',       { method: 'POST' }); toast('Right →'); }
                function startSweep()  { fetch('/api/motor/sweep/start', { method: 'POST' }); toast('Sweep started'); }
                function stopSweep()   { fetch('/api/motor/sweep/stop',  { method: 'POST' }); toast('Sweep stopped'); }

                // ---- Keyboard shortcuts ----
                document.addEventListener('keydown', (e) => {
                    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
                    switch (e.key) {
                        case 'ArrowLeft':  e.preventDefault(); motorLeft();   break;
                        case 'ArrowRight': e.preventDefault(); motorRight();  break;
                        case 'c': case 'C': motorCenter(); break;
                        case 's': case 'S': startSweep(); break;
                        case 'x': case 'X': stopSweep();  break;
                    }
                });

                // ---- Audio (placeholder) ----
                function toggleListen() {
                    isListening = !isListening;
                    if (isTalking && isListening) { isTalking = false; updateTalkUI(); }
                    listenBtn.textContent = isListening ? '🔇 STOP' : '🔊 LISTEN';
                    listenBtn.classList.toggle('active', isListening);
                    updateAudioStatus();
                    toast(isListening ? 'Listening (placeholder)' : 'Listen stopped', 'warn');
                }

                function toggleTalk() {
                    isTalking = !isTalking;
                    if (isListening && isTalking) { isListening = false; updateListenUI(); }
                    updateTalkUI();
                    updateAudioStatus();
                    toast(isTalking ? 'Mic open (placeholder)' : 'Mic closed', 'warn');
                }

                function updateListenUI() {
                    listenBtn.textContent = '🔊 LISTEN';
                    listenBtn.classList.remove('active');
                }

                function updateTalkUI() {
                    talkBtn.textContent = isTalking ? '🔴 STOP' : '🎤 TALK';
                    talkBtn.classList.toggle('active', isTalking);
                }

                function updateAudioStatus() {
                    if (isTalking) {
                        audioStatus.textContent = 'TALKING';
                        audioStatus.style.cssText = 'background:#bf360c;color:#ffccbc;border:1px solid #e64a19;';
                    } else if (isListening) {
                        audioStatus.textContent = 'LISTENING';
                        audioStatus.style.cssText = 'background:#004d40;color:#80cbc4;border:1px solid #00695c;';
                    } else {
                        audioStatus.textContent = 'AUDIO OFF';
                        audioStatus.style.cssText = 'background:#1a1a1a;color:#444;border:1px solid #2a2a2a;';
                    }
                }

                setInterval(checkRecordingStatus, 5000);
                connect();
            </script>
        </body>
        </html>
        """;
}
