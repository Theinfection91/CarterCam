using System;
using System.Diagnostics;

namespace CarterCam.API.Services
{
    public class EncoderLauncher
    {
        private readonly string _exePath;
        private Process? _process;

        public EncoderLauncher(string exePath)
        {
            _exePath = exePath;
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited)
            {
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start encoder process");

            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Console.WriteLine("[Encoder] Process exited.");

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"[Encoder] {e.Data}");
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Console.WriteLine($"[Encoder ERR] {e.Data}");
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Console.WriteLine("[Encoder] Started, waiting for pipe connection...");
        }
    }
}