using OpenCvSharp;

namespace CarterCam.API.Services
{
    public class PersonTracker
    {
        private readonly TCPServer _tcp;
        private readonly HOGDescriptor _hog;

        private const double DeadZone  = 0.10;
        private const int    GraceMs   = 800;
        private const double EmaAlpha  = 0.4;

        // Search sweep — slow pan when nobody is visible
        private const int SearchStepMs   = 300;  // how often to nudge during search
        private const int SearchFlipSteps = 18;  // nudges before reversing search direction

        private DateTime _lastCommand    = DateTime.MinValue;
        private DateTime _lastDetected   = DateTime.MinValue;
        private DateTime _lastSearchStep = DateTime.MinValue;
        private char     _lastDirection  = '\0';
        private double?  _smoothedOffset = null;

        private bool _searching      = false;
        private char _searchDir      = 'R';
        private int  _searchStepCount = 0;

        public bool Enabled { get; set; } = false;

        public PersonTracker(TCPServer tcp)
        {
            _tcp = tcp;
            _hog = new HOGDescriptor();
            _hog.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());
        }

        public void ProcessFrame(byte[] jpegBytes)
        {
            if (!Enabled) return;

            using var mat = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
            if (mat.Empty()) return;

            using var small = new Mat();
            Cv2.Resize(mat, small, new Size(320, 240));

            Rect[] people = _hog.DetectMultiScale(
                small,
                hitThreshold: -0.5,
                winStride: new Size(4, 4),
                padding: new Size(8, 8),
                scale: 1.05
            );

            if (people.Length > 0)
            {
                // Person found — cancel search, lock on
                if (_searching)
                {
                    _searching = false;
                    Console.WriteLine("[Tracker] Person acquired, leaving search mode");
                }

                var person     = people.OrderByDescending(p => p.Width * p.Height).First();
                double rawOffset = (person.X + person.Width / 2.0) / small.Width - 0.5;

                _smoothedOffset = _smoothedOffset.HasValue
                    ? EmaAlpha * rawOffset + (1 - EmaAlpha) * _smoothedOffset.Value
                    : rawOffset;

                double offset = _smoothedOffset.Value;
                char cmd = '\0';

                if      (offset < -DeadZone) cmd = 'L';
                else if (offset >  DeadZone) cmd = 'R';

                if (cmd != '\0')
                {
                    _lastDirection = cmd;
                    _lastDetected  = DateTime.UtcNow;

                    int throttle = (int)Math.Max(80, 200 - Math.Abs(offset) * 600);
                    if ((DateTime.UtcNow - _lastCommand).TotalMilliseconds >= throttle)
                    {
                        _tcp.SendMotorCommand(cmd);
                        _lastCommand = DateTime.UtcNow;
                        Console.WriteLine($"[Tracker] offset={offset:F2} → {cmd} (throttle={throttle}ms)");
                    }
                }
            }
            else
            {
                bool inGrace = (DateTime.UtcNow - _lastDetected).TotalMilliseconds < GraceMs;

                if (inGrace && _lastDirection != '\0')
                {
                    // Coast in last known direction
                    if ((DateTime.UtcNow - _lastCommand).TotalMilliseconds >= 200)
                    {
                        _tcp.SendMotorCommand(_lastDirection);
                        _lastCommand = DateTime.UtcNow;
                        Console.WriteLine($"[Tracker] Coasting {_lastDirection}");
                    }
                }
                else
                {
                    // Grace expired — enter search mode
                    _smoothedOffset = null;

                    if (!_searching)
                    {
                        _searching       = true;
                        _searchStepCount = 0;
                        _searchDir       = _lastDirection == 'L' ? 'R' : 'L'; // search opposite of where we lost them
                        Console.WriteLine($"[Tracker] Lost person — entering search mode ({_searchDir})");
                    }

                    if ((DateTime.UtcNow - _lastSearchStep).TotalMilliseconds >= SearchStepMs)
                    {
                        _tcp.SendMotorCommand(_searchDir);
                        _lastCommand    = DateTime.UtcNow;
                        _lastSearchStep = DateTime.UtcNow;
                        _searchStepCount++;

                        // Reverse direction after N steps (ping-pong sweep)
                        if (_searchStepCount >= SearchFlipSteps)
                        {
                            _searchDir       = _searchDir == 'R' ? 'L' : 'R';
                            _searchStepCount = 0;
                            Console.WriteLine($"[Tracker] Search reversing → {_searchDir}");
                        }
                    }
                }
            }
        }
    }
}
