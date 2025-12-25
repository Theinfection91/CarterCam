using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace CarterCam.API.Services
{
    public class FrameBroadcaster
    {
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private byte[]? _latestFrame;
        private readonly object _frameLock = new();

        public void AddClient(string id, WebSocket socket)
        {
            _clients[id] = socket;
            Console.WriteLine($"[WS] Client connected: {id} (Total: {_clients.Count})");
        }

        public void RemoveClient(string id)
        {
            _clients.TryRemove(id, out _);
            Console.WriteLine($"[WS] Client disconnected: {id} (Total: {_clients.Count})");
        }

        public void BroadcastFrame(byte[] frame)
        {
            lock (_frameLock)
            {
                _latestFrame = frame;
            }

            foreach (var (id, socket) in _clients)
            {
                if (socket.State == WebSocketState.Open)
                {
                    _ = SendFrameAsync(id, socket, frame);
                }
                else
                {
                    RemoveClient(id);
                }
            }
        }

        private async Task SendFrameAsync(string id, WebSocket socket, byte[] frame)
        {
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(frame),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
            }
            catch
            {
                RemoveClient(id);
            }
        }

        public byte[]? GetLatestFrame()
        {
            lock (_frameLock)
            {
                return _latestFrame;
            }
        }

        public int ClientCount => _clients.Count;
    }
}