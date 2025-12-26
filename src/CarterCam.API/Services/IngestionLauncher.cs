using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace CarterCam.API.Services
{
    public class IngestionLauncher
    {
        private readonly string _exePath;
        private readonly object _stdinSync = new();
        private Process? _process;
        private Stream? _stdinStream;

        public IngestionLauncher(string exePath)
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
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ingestion process");

            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                lock (_stdinSync)
                {
                    _stdinStream = null;
                }
                Console.WriteLine("[Ingestor] Process exited.");
            };

            // Redirect stdout to C# console
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"[Ingestor] {e.Data}");
                }
            };

            // Redirect stderr to C# console
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"[Ingestor ERR] {e.Data}");
                }
            };

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _stdinStream = _process.StandardInput.BaseStream;
            Console.WriteLine("[Ingestor] Engine started, output redirected to this console.");
        }

        public void EnsureProcess()
        {
            if (_process == null || _process.HasExited)
            {
                Console.WriteLine("[Ingestor] Restarting ingestion process...");
                Start();
            }
        }

        public void SendFrame(byte[] frame)
        {
            if (frame is not { Length: > 0 })
            {
                return;
            }

            EnsureProcess();

            lock (_stdinSync)
            {
                if (_stdinStream == null)
                {
                    throw new InvalidOperationException("Ingestion stdin is not available.");
                }

                Span<byte> prefix = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, frame.Length);

                try
                {
                    _stdinStream.Write(prefix);
                    _stdinStream.Write(frame, 0, frame.Length);
                    _stdinStream.Flush();
                }
                catch (IOException ioEx)
                {
                    Console.WriteLine($"[Ingestor] Failed to write frame: {ioEx.Message}");
                    _stdinStream = null;
                }
            }
        }
    }
}
