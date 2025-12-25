using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace CarterCam.API.Services
{
    public class TCPServer
    {
        private readonly int _port;
        private readonly IngestionLauncher _ingestionLauncher;
        private TcpListener? _listener;
        private int _frameCount;
        private readonly Stopwatch _stopwatch = new();
        
        // Queue to decouple receiving from processing
        private readonly BlockingCollection<byte[]> _frameQueue = new(boundedCapacity: 5);

        public TCPServer(int port, IngestionLauncher ingestionLauncher)
        {
            _port = port;
            _ingestionLauncher = ingestionLauncher;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            Console.WriteLine($"TCP Server started on port {_port}.");
            
            // Start frame processing on separate thread
            Task.Run(ProcessFrames);
            
            ListenForClients();
        }

        private void ProcessFrames()
        {
            foreach (var frame in _frameQueue.GetConsumingEnumerable())
            {
                try
                {
                    _ingestionLauncher.SendFrame(frame);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TCP] Frame processing error: {ex.Message}");
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
            
            // Increase receive buffer
            client.ReceiveBufferSize = 65536;
            
            using NetworkStream stream = client.GetStream();

            byte[] lengthBuffer = new byte[4];
            _frameCount = 0;
            _stopwatch.Restart();

            try
            {
                while (true)
                {
                    // Read length prefix
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

                    // Non-blocking enqueue - drop frame if queue is full
                    if (!_frameQueue.TryAdd(frameData))
                    {
                        // Queue full, drop oldest and add new
                        _frameQueue.TryTake(out _);
                        _frameQueue.TryAdd(frameData);
                    }

                    // Log FPS every 30 frames
                    if (_frameCount % 30 == 0)
                    {
                        double fps = _frameCount / _stopwatch.Elapsed.TotalSeconds;
                        Console.WriteLine($"[TCP] Received {_frameCount} frames, {fps:F1} fps, queue: {_frameQueue.Count}");
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
