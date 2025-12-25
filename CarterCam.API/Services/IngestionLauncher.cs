using System;
using System.Diagnostics;

namespace CarterCam.API.Services
{
    public class IngestionLauncher
    {
        private readonly string _exePath;
        private Process? _process;

        public IngestionLauncher(string exePath)
        {
            _exePath = exePath;
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited) return;

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = true,   // Open in its own window
                CreateNoWindow = false,   // Show the console window
            };

            _process = Process.Start(psi);
            if (_process == null)
                throw new Exception("Failed to start ingestion process");

            Console.WriteLine("Ingestion engine started in its own console");
        }

        public void EnsureProcess()
        {
            if (_process == null || _process.HasExited)
            {
                Console.WriteLine("Restarting ingestion process...");
                Start();
            }
        }

        // Sending frames directly to its stdin is no longer possible
        public void SendFrame(byte[] frame)
        {
            Console.WriteLine("Standalone console: cannot send frame directly. Use TCP or another IPC method.");
        }
    }
}
