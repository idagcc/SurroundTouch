using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SurroundTouch
{
    // Optional, read-only vendor diagnostics. The independent remapper never
    // relies on this backend, its license, or any UPDD mutation command.
    public sealed class UpddBackend
    {
        private readonly string utilityPath;
        private readonly object sync = new object();
        private UpddBackend(string path) { utilityPath = path; }
        public string UtilityPath { get { return utilityPath; } }

        public static bool TryProbe(out UpddBackend backend, out UpddProbe probe, out string error)
        {
            backend = null;
            probe = null;
            error = null;
            try
            {
                string path = FindUtility();
                if (path == null) { error = "UPDD is not installed in a standard Program Files location."; return false; }
                UpddBackend candidate = new UpddBackend(path);
                probe = candidate.Inspect();
                backend = candidate;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public UpddProbe Inspect()
        {
            lock (sync)
            {
                UpddProbe result = new UpddProbe();
                result.UtilityPath = utilityPath;
                result.Version = FileVersionInfo.GetVersionInfo(utilityPath).FileVersion;
                if (String.IsNullOrWhiteSpace(result.Version)) result.Version = "unknown";
                result.RawDevices = Run("devices");
                result.Devices = ParseDevices(result.RawDevices);
                foreach (UpddSetting setting in ReadSettings(0, "registration.*.is_eval"))
                    if (setting.Value.Trim() == "1") result.IsEvaluation = true;
                foreach (UpddSetting setting in ReadSettings(0, "license.*.eval_click_limit"))
                {
                    int limit;
                    if (Int32.TryParse(setting.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) && limit > 0)
                        if (result.EvaluationTouchLimit == 0 || limit < result.EvaluationTouchLimit) result.EvaluationTouchLimit = limit;
                }
                result.LicenseSummary = result.IsEvaluation
                    ? (result.EvaluationTouchLimit > 0 ? "UPDD evaluation license; configured touch limit: " + result.EvaluationTouchLimit + "." : "UPDD evaluation license detected.")
                    : "UPDD license status was not established by this diagnostic check.";
                return result;
            }
        }

        public UpddSnapshot CaptureSettings()
        {
            lock (sync)
            {
                UpddProbe probe = Inspect();
                UpddSnapshot result = new UpddSnapshot();
                result.SchemaVersion = 1;
                result.CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                result.Probe = probe;
                result.Settings = new List<UpddSetting>();
                // Preserve empty-but-explicit values and absence separately.
                // No activation keys or unrelated driver settings are exported.
                foreach (string key in new string[] { "monitor_segment_map", "monitor_segment_map.1", "use_native_screen_rotation" })
                    result.Settings.Add(ReadOne(0, key));
                foreach (UpddDevice device in probe.Devices)
                    foreach (string key in new string[] { "monitor_number", "monitor_segment", "monitor_bind.display_name", "monitor_bind.display_name.v7", "interlock", "interlock_release_time", "active_touch_interface", "enabled", "connection_key", "location", "hardware_id" })
                        result.Settings.Add(ReadOne(device.Id, key));
                return result;
            }
        }

        public void SaveSnapshot(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a snapshot file path.", "path");
            UpddSnapshot snapshot = CaptureSettings();
            // CreateNew prevents an accidental overwrite of an existing backup.
            using (FileStream stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(UpddSnapshot)).WriteObject(stream, snapshot);
        }

        public void OpenConsole()
        {
            string path = Path.Combine(Path.GetDirectoryName(utilityPath), "UPDD Console.exe");
            AssertOrdinaryInstalledFile(path);
            // This explicitly opens the vendor's interactive configuration UI.
            // No setting, calibration, or restart command is sent by this app.
            Process.Start(new ProcessStartInfo { FileName = path, WorkingDirectory = Path.GetDirectoryName(path), UseShellExecute = true });
        }

        private UpddSetting ReadOne(int deviceId, string key)
        {
            List<UpddSetting> values = ReadSettings(deviceId, key);
            foreach (UpddSetting value in values)
                if (String.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)) return value;
            return new UpddSetting { DeviceId = deviceId, Key = key, Present = false, Value = null };
        }

        private List<UpddSetting> ReadSettings(int deviceId, string key)
        {
            string raw = deviceId == 0
                ? Run("global", "get", key)
                : Run("device", deviceId.ToString(CultureInfo.InvariantCulture), "get", key);
            return ParseSettings(deviceId, raw);
        }

        private string Run(params string[] arguments)
        {
            AssertOrdinaryInstalledFile(utilityPath);
            bool readOnly = arguments.Length == 1 && arguments[0] == "devices";
            readOnly = readOnly || (arguments.Length == 3 && arguments[0] == "global" && arguments[1] == "get");
            readOnly = readOnly || (arguments.Length == 4 && arguments[0] == "device" && arguments[2] == "get" && Regex.IsMatch(arguments[1], @"^\d+$"));
            if (!readOnly) throw new InvalidOperationException("Only read-only UPDD diagnostic commands are permitted.");
            List<string> quoted = new List<string>();
            foreach (string argument in arguments)
            {
                // This adapter only issues devices/get with fixed numeric/key args.
                // Restrict tokens rather than passing shell-interpreted command text.
                if (!Regex.IsMatch(argument, @"^[A-Za-z0-9_.*-]+$")) throw new InvalidOperationException("Invalid UPDD diagnostic argument.");
                quoted.Add("\"" + argument + "\"");
            }
            ProcessStartInfo start = new ProcessStartInfo {
                FileName = utilityPath, Arguments = String.Join(" ", quoted.ToArray()),
                WorkingDirectory = Path.GetDirectoryName(utilityPath), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process process = new Process { StartInfo = start })
            {
                if (!process.Start()) throw new InvalidOperationException("UPDD diagnostics did not start.");
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("UPDD did not respond within 10 seconds. No settings were changed.");
                }
                string stdout = output.GetAwaiter().GetResult();
                string stderr = errors.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("UPDD diagnostics failed (" + process.ExitCode + "): " + Limit(stderr.Length == 0 ? stdout : stderr));
                if (stdout.Length > 1048576) throw new InvalidDataException("UPDD returned an unexpectedly large diagnostic response.");
                return stdout;
            }
        }

        private static string Limit(string value) { return value.Length <= 1000 ? value.Trim() : value.Substring(0, 1000).Trim(); }

        private static string FindUtility()
        {
            foreach (Environment.SpecialFolder folder in new Environment.SpecialFolder[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(folder);
                if (String.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "UPDD", "upddutils.exe");
                if (File.Exists(path)) { AssertOrdinaryInstalledFile(path); return path; }
            }
            return null;
        }

        private static void AssertOrdinaryInstalledFile(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The UPDD utility was not found.", path);
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to launch UPDD through a redirected or linked path.");
                current = Path.GetDirectoryName(current);
            }
        }

        public static List<UpddDevice> ParseDevices(string raw)
        {
            List<UpddDevice> devices = new List<UpddDevice>();
            HashSet<int> seen = new HashSet<int>();
            using (StringReader reader = new StringReader(raw ?? String.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    Match match = Regex.Match(line, @"^\s*(\d+)\s+(\S+)\s+(.+)$");
                    if (!match.Success) throw new InvalidDataException("Unrecognized UPDD device-list format: " + Limit(line));
                    int id;
                    if (!Int32.TryParse(match.Groups[1].Value, out id) || id < 1 || !seen.Add(id))
                        throw new InvalidDataException("Invalid or duplicate UPDD device ID.");
                    string description = match.Groups[3].Value;
                    int boundary = description.IndexOf('\t');
                    if (boundary < 0)
                    {
                        Match monitor = Regex.Match(description, @"\s+Monitor\s");
                        if (monitor.Success) boundary = monitor.Index;
                    }
                    devices.Add(new UpddDevice {
                        Id = id, Status = match.Groups[2].Value,
                        Name = boundary < 0 ? description.Trim() : description.Substring(0, boundary).Trim(),
                        Monitor = boundary < 0 ? String.Empty : description.Substring(boundary).Trim()
                    });
                }
            }
            return devices;
        }

        public static List<UpddSetting> ParseSettings(int deviceId, string raw)
        {
            List<UpddSetting> values = new List<UpddSetting>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (StringReader reader = new StringReader(raw ?? String.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    int colon = line.IndexOf(':');
                    if (colon < 1) throw new InvalidDataException("Unrecognized UPDD setting format: " + Limit(line));
                    string key = line.Substring(0, colon).Trim();
                    if (!Regex.IsMatch(key, @"^[A-Za-z0-9_.-]+$") || !seen.Add(key))
                        throw new InvalidDataException("Invalid or duplicate UPDD setting name.");
                    string value = line.Substring(colon + 1);
                    if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
                    values.Add(new UpddSetting { DeviceId = deviceId, Key = key, Present = true, Value = value });
                }
            }
            return values;
        }
    }

    [DataContract]
    public sealed class UpddDevice
    {
        [DataMember] public int Id;
        [DataMember] public string Status;
        [DataMember] public string Name;
        [DataMember] public string Monitor;
        public bool Connected { get { return String.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase); } }
        public override string ToString() { return "Controller " + Id + " — " + Name + " (" + Status + ")"; }
    }

    [DataContract]
    public sealed class UpddProbe
    {
        [DataMember] public string UtilityPath;
        [DataMember] public string Version;
        [DataMember] public string RawDevices;
        [DataMember] public List<UpddDevice> Devices;
        [DataMember] public bool IsEvaluation;
        [DataMember] public int EvaluationTouchLimit;
        [DataMember] public string LicenseSummary;
    }

    [DataContract]
    public sealed class UpddSetting
    {
        [DataMember] public int DeviceId; // 0 is global; positive numbers are explicit device handles.
        [DataMember] public string Key;
        [DataMember] public bool Present;
        [DataMember] public string Value;
    }

    [DataContract]
    public sealed class UpddSnapshot
    {
        [DataMember] public int SchemaVersion;
        [DataMember] public string CapturedUtc;
        [DataMember] public UpddProbe Probe;
        [DataMember] public List<UpddSetting> Settings;
    }
}
