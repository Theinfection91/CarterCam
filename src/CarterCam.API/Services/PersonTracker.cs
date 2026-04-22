using OpenCvSharp;

namespace CarterCam.API.Services
{
    public class PersonTracker
    {
        private readonly TCPServer _tcp;
        private readonly HOGDescriptor _hog;

        private const double DeadZone  = 0.15;
        private const int    ThrottleMs = 400;
        private DateTime _lastCommand = DateTime.MinValue;

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
            if ((DateTime.UtcNow - _lastCommand).TotalMilliseconds < ThrottleMs) return;

            using var mat = Cv2.ImDecode(jpegBytes, ImreadModes.Color);
            if (mat.Empty()) return;

            // Resize to speed up detection — QVGA is already 320x240, this is fine
            using var small = new Mat();
            Cv2.Resize(mat, small, new Size(320, 240));

            Rect[] people = _hog.DetectMultiScale(
                small,
                hitThreshold: 0,
                winStride: new Size(8, 8),
                padding: new Size(4, 4),
                scale: 1.05
            );

            if (people.Length == 0) return;

            // Largest detected person
            var person       = people.OrderByDescending(p => p.Width * p.Height).First();
            double centerX   = person.X + person.Width / 2.0;
            double offset    = (centerX / small.Width) - 0.5;

            char cmd;
            if      (offset < -DeadZone) cmd = 'L';
            else if (offset >  DeadZone) cmd = 'R';
            else                         return;

            _tcp.SendMotorCommand(cmd);
            _lastCommand = DateTime.UtcNow;
            Console.WriteLine($"[Tracker] Person offset={offset:F2} → {cmd}");
        }
    }
}
