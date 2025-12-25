using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CarterCam.API.Services
{
    public class TCPServer
    {
        private readonly int _port;
        private readonly IngestionLauncher _ingestionLauncher;
        private TcpListener? _listener;

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
            ListenForClients();
        }

        private async void ListenForClients()
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleClient(client);
            }
        }

        private async Task HandleClient(TcpClient client)
        {
            Console.WriteLine($"Client connected: {client.Client.RemoteEndPoint}");
            using NetworkStream stream = client.GetStream();

            byte[] lengthBuffer = new byte[4];

            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(lengthBuffer, 0, 4);
                    if (read == 0) break;

                    int frameLength = BitConverter.ToInt32(lengthBuffer, 0);
                    byte[] frameData = new byte[frameLength];
                    int totalRead = 0;

                    while (totalRead < frameLength)
                    {
                        int bytesRead = await stream.ReadAsync(frameData, totalRead, frameLength - totalRead);
                        if (bytesRead == 0) break;
                        totalRead += bytesRead;
                    }

                    if (totalRead == frameLength)
                    {
                        Console.WriteLine($"TCP received frame of {frameLength} bytes");

                        // Forward frame to ingestion engine
                        _ingestionLauncher.SendFrame(frameData);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }

            Console.WriteLine($"Client disconnected: {client.Client.RemoteEndPoint}");
        }
    }
}
