using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace CarterCam.API.Services
{
    public class TCPServer
    {
        private readonly int _port;
        private readonly FrameBroadcaster _broadcaster;
        private readonly IngestionLauncher _ingestor;
        private TcpListener? _listener;
        private int _frameCount;
        private readonly Stopwatch _stopwatch = new();
        
        private readonly BlockingCollection<byte[]> _recordingQueue = new(boundedCapacity: 10);
        
        public bool IsRecording { get; private set; }

        public TCPServer(int port, FrameBroadcaster broadcaster, IngestionLauncher ingestor)
        {
            _port = port;
            _broadcaster = broadcaster;
            _ingestor = ingestor;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            Console.WriteLine($"TCP Server started on port {_port}.");
            
            // Start recording processor thread
            Task.Run(ProcessRecordingQueue);
            
            ListenForClients();
        }

        public void StartRecording()
        {
            if (IsRecording) return;
            
            _ingestor.Start();
            IsRecording = true;
            Console.WriteLine("[Recording] Started");
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            
            IsRecording = false;
            Console.WriteLine("[Recording] Stopped");
        }

        private void ProcessRecordingQueue()
        {
            foreach (var frame in _recordingQueue.GetConsumingEnumerable())
            {
                if (IsRecording)
                {
                    try
                    {
                        _ingestor.SendFrame(frame);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Recording] Error: {ex.Message}");
                    }
                }
            }
        }

        private async void ListenForClients()
        {
            while (true)
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = HandleClient(client);
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");
            client.ReceiveBufferSize = 65536;
            
            using NetworkStream stream = client.GetStream();
            byte[] lengthBuffer = new byte[4];
            _frameCount = 0;
            _stopwatch.Restart();

            try
            {
                while (true)
                {
                    int read = await ReadExactAsync(stream, lengthBuffer, 4);
                    if (read == 0) break;

                    int frameLength = BitConverter.ToInt32(lengthBuffer, 0);
                    
                    if (frameLength <= 0 || frameLength > 1_000_000)
                    {
                        Console.WriteLine($"[TCP] Invalid frame length: {frameLength}");
                        break;
                    }

                    byte[] frameData = new byte[frameLength];
                    read = await ReadExactAsync(stream, frameData, frameLength);
                    if (read == 0) break;

                    _frameCount++;

                    // Always broadcast to WebSocket (live streaming)
                    _broadcaster.BroadcastFrame(frameData);

                    // Queue for recording if enabled (non-blocking)
                    if (IsRecording)
                    {
                        _recordingQueue.TryAdd(frameData);
                    }

                    if (_frameCount % 30 == 0)
                    {
                        double fps = _frameCount / _stopwatch.Elapsed.TotalSeconds;
                        var recStatus = IsRecording ? "🔴 REC" : "⏹️";
                        Console.WriteLine($"[TCP] {_frameCount} frames, {fps:F1} fps, WS: {_broadcaster.ClientCount} {recStatus}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }

            Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint}");
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead));
                if (read == 0) return 0;
                totalRead += read;
            }
            return totalRead;
        }
    }
}
