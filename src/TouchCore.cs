using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SurroundTouch
{
    public struct PixelPoint : IEquatable<PixelPoint>
    {
        public int X;
        public int Y;
        public PixelPoint(int x, int y) { X = x; Y = y; }
        public bool Equals(PixelPoint other) { return X == other.X && Y == other.Y; }
        public override bool Equals(object obj) { return obj is PixelPoint && Equals((PixelPoint)obj); }
        public override int GetHashCode() { unchecked { return (X * 397) ^ Y; } }
        public static bool operator ==(PixelPoint a, PixelPoint b) { return a.Equals(b); }
        public static bool operator !=(PixelPoint a, PixelPoint b) { return !a.Equals(b); }
        public override string ToString() { return X + ", " + Y; }
    }

    public struct PixelRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public PixelRect(int x, int y, int width, int height)
        { X = x; Y = y; Width = width; Height = height; }
        public override string ToString() { return X + ", " + Y + "; " + Width + " x " + Height; }
    }

    /// <summary>
    /// Maps physical pixel coordinates in a controller's reported display rectangle
    /// to its assigned part of the desktop. Source is runtime information, never saved.
    /// Crop values are normalized source bounds. Flips occur before clockwise rotation.
    /// </summary>
    public sealed class Mapping
    {
        public string DevicePath;
        [XmlIgnore]
        public PixelRect Source;
        public PixelRect Target;
        public int Rotation;
        public bool FlipX;
        public bool FlipY;
        public double MinX = 0;
        public double MinY = 0;
        public double MaxX = 1;
        public double MaxY = 1;

        public void Validate()
        {
            ValidateRectangle(Source, "Source");
            ValidateRectangle(Target, "Target");
            if (Rotation != 0 && Rotation != 90 && Rotation != 180 && Rotation != 270)
                throw new ArgumentException("Rotation must be 0, 90, 180, or 270 degrees.");
            ValidateCrop(MinX, MaxX, "X");
            ValidateCrop(MinY, MaxY, "Y");
        }

        private static void ValidateRectangle(PixelRect rect, string name)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new ArgumentException(name + " must have a positive width and height.");
            if ((long)rect.X + rect.Width - 1 > Int32.MaxValue ||
                (long)rect.Y + rect.Height - 1 > Int32.MaxValue)
                throw new ArgumentException(name + " extends outside supported pixel coordinates.");
        }

        private static void ValidateCrop(double min, double max, string axis)
        {
            if (Double.IsNaN(min) || Double.IsNaN(max) || Double.IsInfinity(min) ||
                Double.IsInfinity(max) || min < 0 || max > 1 || min >= max)
                throw new ArgumentException(axis + " crop bounds must satisfy 0 <= minimum < maximum <= 1.");
        }

        public PixelPoint Map(PixelPoint point)
        {
            Validate();
            double x = Source.Width == 1 ? 0 : ((double)point.X - Source.X) / (Source.Width - 1);
            double y = Source.Height == 1 ? 0 : ((double)point.Y - Source.Y) / (Source.Height - 1);
            x = Clamp((x - MinX) / (MaxX - MinX));
            y = Clamp((y - MinY) / (MaxY - MinY));
            if (FlipX) x = 1 - x;
            if (FlipY) y = 1 - y;
            double oldX = x;
            if (Rotation == 90) { x = 1 - y; y = oldX; }
            else if (Rotation == 180) { x = 1 - x; y = 1 - y; }
            else if (Rotation == 270) { x = y; y = 1 - oldX; }
            return new PixelPoint(
                (int)((long)Target.X + (long)Math.Round(x * (Target.Width - 1), MidpointRounding.AwayFromZero)),
                (int)((long)Target.Y + (long)Math.Round(y * (Target.Height - 1), MidpointRounding.AwayFromZero)));
        }

        private static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }
    }

    public enum TouchPhase { Down, Move, Up, Cancel }

    public sealed class TouchSample
    {
        public string Device;
        public uint Id;
        public PixelPoint Position;
        public TouchPhase Phase;
        public uint Pressure = 512;
    }

    public sealed class InjectedContact
    {
        public uint Id;
        public PixelPoint Position;
        public TouchPhase Phase;
        public uint Pressure;
    }

    /// <summary>
    /// Single-threaded contact merger. The caller maps samples before submitting them,
    /// invokes Tick every 30 ms, and translates Cancel into UP | CANCELED.
    /// Every emitted frame contains every active contact, even stationary contacts on
    /// other controllers. State advances only after the sink successfully returns.
    /// </summary>
    public sealed class TouchPipeline
    {
        public const int MaximumContacts = 256;
        private readonly Action<InjectedContact[]> send;
        private Dictionary<ContactKey, ContactState> active = new Dictionary<ContactKey, ContactState>();

        public TouchPipeline(Action<InjectedContact[]> send)
        {
            if (send == null) throw new ArgumentNullException("send");
            this.send = send;
        }

        public int ActiveCount { get { return active.Count; } }

        public void Process(IEnumerable<TouchSample> samples)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            // Materialize first: a malformed input cannot partially update state.
            List<TouchSample> copy = new List<TouchSample>();
            foreach (TouchSample item in samples)
            {
                if (item == null) throw new ArgumentException("A touch sample is null.", "samples");
                if (String.IsNullOrEmpty(item.Device)) throw new ArgumentException("A touch sample has no device identity.", "samples");
                if (item.Phase < TouchPhase.Down || item.Phase > TouchPhase.Cancel)
                    throw new ArgumentException("A touch sample has an unknown phase.", "samples");
                copy.Add(new TouchSample { Device = item.Device, Id = item.Id, Position = item.Position,
                    Phase = item.Phase, Pressure = Math.Min(1024u, item.Pressure) });
            }
            // A repeated key denotes a later event, not another member of one frame.
            // This preserves Down -> Move -> Up when several events are delivered at once.
            List<TouchSample> frame = new List<TouchSample>();
            HashSet<ContactKey> keys = new HashSet<ContactKey>();
            foreach (TouchSample sample in copy)
            {
                ContactKey key = new ContactKey(sample.Device, sample.Id);
                if (keys.Contains(key)) { ProcessFrame(frame); frame.Clear(); keys.Clear(); }
                frame.Add(sample);
                keys.Add(key);
            }
            ProcessFrame(frame);
        }

        private void ProcessFrame(List<TouchSample> samples)
        {
            if (samples.Count == 0) return;
            int additions = 0;
            bool recognized = false;
            foreach (TouchSample sample in samples)
            {
                bool exists = active.ContainsKey(new ContactKey(sample.Device, sample.Id));
                if (exists || sample.Phase == TouchPhase.Down) recognized = true;
                if (!exists && sample.Phase == TouchPhase.Down) additions++;
            }
            if (!recognized) return;
            // Ended contacts must remain in the outgoing frame; their IDs cannot be
            // reused by a new contact until their release frame has succeeded.
            if (active.Count + additions > MaximumContacts)
                throw new InvalidOperationException("This touch frame exceeds the supported maximum of 256 contacts.");

            Dictionary<ContactKey, ContactState> beforeRelease = Clone(active);
            bool needsMove = false;
            foreach (TouchSample sample in samples)
            {
                ContactState state;
                if (sample.Phase == TouchPhase.Up && beforeRelease.TryGetValue(new ContactKey(sample.Device, sample.Id), out state)
                    && state.Position != sample.Position)
                {
                    state.Position = sample.Position;
                    state.Pressure = sample.Pressure;
                    needsMove = true;
                }
            }
            if (needsMove)
            {
                // Windows requires UP at exactly the last successfully injected position.
                // Move there first, then commit it independently of the later release.
                send(BuildFrame(beforeRelease, null));
                active = beforeRelease;
            }

            Dictionary<ContactKey, ContactState> next = Clone(active);
            Dictionary<ContactKey, TouchPhase> phases = new Dictionary<ContactKey, TouchPhase>();
            List<ContactKey> ended = new List<ContactKey>();
            foreach (TouchSample sample in samples)
            {
                ContactKey key = new ContactKey(sample.Device, sample.Id);
                ContactState state;
                bool exists = next.TryGetValue(key, out state);
                if (sample.Phase == TouchPhase.Down)
                {
                    if (!exists)
                    {
                        state = new ContactState { Id = AllocateId(next) };
                        next.Add(key, state);
                        phases[key] = TouchPhase.Down;
                    }
                    // Duplicate DOWN reports retain the existing injected contact.
                    state.Position = sample.Position;
                    state.Pressure = sample.Pressure;
                }
                else if (exists && sample.Phase == TouchPhase.Move)
                {
                    state.Position = sample.Position;
                    state.Pressure = sample.Pressure;
                }
                else if (exists && (sample.Phase == TouchPhase.Up || sample.Phase == TouchPhase.Cancel))
                {
                    // Cancel also releases at the previous position, without inventing motion.
                    phases[key] = sample.Phase;
                    ended.Add(key);
                }
            }
            InjectedContact[] output = BuildFrame(next, phases);
            send(output);
            foreach (ContactKey key in ended) next.Remove(key);
            active = next;
        }

        public void Tick()
        {
            if (active.Count != 0) send(BuildFrame(active, null));
        }

        public void CancelAll()
        {
            if (active.Count == 0) return;
            Dictionary<ContactKey, TouchPhase> phases = new Dictionary<ContactKey, TouchPhase>();
            foreach (ContactKey key in active.Keys) phases[key] = TouchPhase.Cancel;
            send(BuildFrame(active, phases));
            active.Clear();
        }

        /// <summary>
        /// Releases a captured Windows pointer when its source device is unavailable.
        /// A hardware ID may exist on more than one device, so cancel every matching key.
        /// Other contacts remain active and appear as stationary updates in the frame.
        /// </summary>
        public void CancelPointer(uint inputId)
        {
            Dictionary<ContactKey, TouchPhase> phases = new Dictionary<ContactKey, TouchPhase>();
            foreach (ContactKey key in active.Keys)
                if (key.InputId == inputId) phases.Add(key, TouchPhase.Cancel);
            if (phases.Count == 0) return;
            send(BuildFrame(active, phases));
            foreach (ContactKey key in phases.Keys) active.Remove(key);
        }

        private static uint AllocateId(Dictionary<ContactKey, ContactState> contacts)
        {
            bool[] used = new bool[MaximumContacts + 1];
            foreach (ContactState contact in contacts.Values) used[contact.Id] = true;
            for (uint id = 1; id <= MaximumContacts; id++) if (!used[id]) return id;
            throw new InvalidOperationException("No injection contact ID is available.");
        }

        private static Dictionary<ContactKey, ContactState> Clone(Dictionary<ContactKey, ContactState> original)
        {
            Dictionary<ContactKey, ContactState> copy = new Dictionary<ContactKey, ContactState>();
            foreach (KeyValuePair<ContactKey, ContactState> item in original)
                copy.Add(item.Key, new ContactState { Id = item.Value.Id,
                    Position = item.Value.Position, Pressure = item.Value.Pressure });
            return copy;
        }

        private static InjectedContact[] BuildFrame(Dictionary<ContactKey, ContactState> contacts,
            Dictionary<ContactKey, TouchPhase> phases)
        {
            List<InjectedContact> result = new List<InjectedContact>();
            foreach (KeyValuePair<ContactKey, ContactState> item in contacts)
            {
                TouchPhase phase = TouchPhase.Move;
                TouchPhase requestedPhase;
                if (phases != null && phases.TryGetValue(item.Key, out requestedPhase)) phase = requestedPhase;
                result.Add(new InjectedContact { Id = item.Value.Id, Position = item.Value.Position,
                    Phase = phase, Pressure = item.Value.Pressure });
            }
            result.Sort(delegate(InjectedContact a, InjectedContact b) { return a.Id.CompareTo(b.Id); });
            return result.ToArray();
        }

        private sealed class ContactState
        {
            public uint Id;
            public PixelPoint Position;
            public uint Pressure;
        }

        private struct ContactKey : IEquatable<ContactKey>
        {
            private readonly string device;
            private readonly uint id;
            public ContactKey(string device, uint id) { this.device = device; this.id = id; }
            public uint InputId { get { return id; } }
            public bool Equals(ContactKey other) { return id == other.id && StringComparer.OrdinalIgnoreCase.Equals(device, other.device); }
            public override bool Equals(object obj) { return obj is ContactKey && Equals((ContactKey)obj); }
            public override int GetHashCode() { unchecked { return (StringComparer.OrdinalIgnoreCase.GetHashCode(device) * 397) ^ (int)id; } }
        }
    }
}
