using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VAMLaunchPlugin
{
    // ----------------------------------------------------------------
    //  Feature descriptor for a single actuator (Buttplug v3)
    // ----------------------------------------------------------------
    public class DeviceFeature
    {
        public int Index;
        public string FeatureDescriptor; // e.g. "Vibrate", "Constrict", "Position"
        public string ActuatorType;      // e.g. "Vibrate", "Constrict", "Rotate", "Position"
        public int StepCount;
    }

    // ----------------------------------------------------------------
    //  Device descriptor returned by Buttplug
    // ----------------------------------------------------------------
    public class ButtplugDevice
    {
        public int DeviceIndex;
        public string DeviceName;
        public string DisplayName;

        // Feature lists parsed from DeviceMessages
        public List<DeviceFeature> LinearFeatures  = new List<DeviceFeature>();
        public List<DeviceFeature> ScalarFeatures  = new List<DeviceFeature>();
        public List<DeviceFeature> RotateFeatures  = new List<DeviceFeature>();

        public bool SupportsLinearCmd  { get { return LinearFeatures.Count > 0; } }
        public bool SupportsScalarCmd  { get { return ScalarFeatures.Count > 0; } }
        public bool SupportsRotateCmd  { get { return RotateFeatures.Count > 0; } }

        /// <summary>True when the device exposes rotation as a ScalarCmd (actuator type "Rotate").</summary>
        public bool HasRotateScalarFeature
        {
            get
            {
                foreach (var f in ScalarFeatures)
                {
                    if (f.ActuatorType.ToLower() == "rotate") return true;
                }
                return false;
            }
        }

        /// <summary>True if the device can rotate via either RotateCmd or scalar Rotate.</summary>
        public bool CanRotate { get { return SupportsRotateCmd || HasRotateScalarFeature; } }

        /// <summary>True if the device has any usable feature at all.</summary>
        public bool HasAnyFeature
        {
            get { return SupportsLinearCmd || SupportsScalarCmd || SupportsRotateCmd; }
        }

        public string GetLabel()
        {
            string name = !string.IsNullOrEmpty(DisplayName) ? DisplayName : DeviceName;
            return name + " [" + DeviceIndex + "]";
        }

        public string GetCapabilitySummary()
        {
            var parts = new List<string>();
            if (SupportsLinearCmd) parts.Add("Linear(" + LinearFeatures.Count + ")");
            if (SupportsRotateCmd) parts.Add("Rotate(" + RotateFeatures.Count + ")");
            if (SupportsScalarCmd)
            {
                foreach (var f in ScalarFeatures)
                    parts.Add(f.ActuatorType);
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "(none)";
        }
    }

    // ----------------------------------------------------------------
    //  Intiface / JoyHub client  (Buttplug protocol v3 over WebSocket)
    // ----------------------------------------------------------------
    public class IntifaceClient
    {
        private SimpleWebSocket _ws;
        private int _nextMsgId = 1;
        private float _pingTimer;
        private float _pingInterval; // seconds; 0 = no keepalive required
        private bool _handshakeComplete;

        // --- public state ---
        public List<ButtplugDevice> Devices = new List<ButtplugDevice>();

        public bool IsConnected
        {
            get { return _ws != null && _ws.IsConnected && _handshakeComplete; }
        }

        public string Status { get; private set; }

        // --- callbacks (assign before Connect) ---
        public Action OnDevicesChanged;
        public Action<string> OnStatusChanged;
        public Action<string> OnError;

        public IntifaceClient()
        {
            Status = "Disconnected";
        }

        // ============================================================
        //  Connection
        // ============================================================

        public bool Connect(string host, int port)
        {
            if (_ws != null) Disconnect();

            _ws = new SimpleWebSocket();
            _handshakeComplete = false;
            _pingTimer = 0;
            _pingInterval = 0;
            Devices.Clear();
            _nextMsgId = 1;

            SetStatus("Connecting to " + host + ":" + port + " ...");

            bool ok = _ws.Connect(host, port, "/");
            if (!ok)
            {
                SetStatus("Connection failed");
                return false;
            }

            // Buttplug handshake
            SendMsg("RequestServerInfo",
                "\"ClientName\":\"VAMLaunch\",\"MessageVersion\":3");

            SetStatus("Connected – handshaking…");
            return true;
        }

        public void Disconnect()
        {
            if (_ws != null)
            {
                if (_handshakeComplete)
                {
                    SendMsg("StopAllDevices", "");
                }
                _ws.Close();
                _ws = null;
            }
            _handshakeComplete = false;
            Devices.Clear();
            SetStatus("Disconnected");
        }

        // ============================================================
        //  Device management
        // ============================================================

        public void StartScanning()
        {
            if (!IsConnected) return;
            SendMsg("StartScanning", "");
        }

        public void StopScanning()
        {
            if (!IsConnected) return;
            SendMsg("StopScanning", "");
        }

        public void RequestDeviceList()
        {
            if (!IsConnected) return;
            SendMsg("RequestDeviceList", "");
        }

        // ============================================================
        //  Device commands
        // ============================================================

        /// <summary>
        /// Send a LinearCmd (stroker / OSR / linear actuator).
        /// position : 0.0 – 1.0
        /// durationMs : movement time in milliseconds
        /// </summary>
        public void SendLinearCmd(int deviceIndex, float position, int durationMs)
        {
            if (!IsConnected) return;

            position = Mathf.Clamp01(position);
            durationMs = Mathf.Max(durationMs, 0);

            string body = string.Format(CultureInfo.InvariantCulture,
                "\"DeviceIndex\":{0},\"Vectors\":[{{\"Index\":0,\"Duration\":{1},\"Position\":{2:F4}}}]",
                deviceIndex, durationMs, position);

            SendMsg("LinearCmd", body);
        }

        /// <summary>
        /// Send a ScalarCmd to one specific feature index.
        /// scalar : 0.0 – 1.0
        /// </summary>
        public void SendScalarCmd(int deviceIndex, int featureIndex, float scalar, string actuatorType)
        {
            if (!IsConnected) return;

            scalar = Mathf.Clamp01(scalar);

            string body = string.Format(CultureInfo.InvariantCulture,
                "\"DeviceIndex\":{0},\"Scalars\":[{{\"Index\":{1},\"Scalar\":{2:F4},\"ActuatorType\":\"{3}\"}}]",
                deviceIndex, featureIndex, scalar, actuatorType);

            SendMsg("ScalarCmd", body);
        }

        /// <summary>
        /// Send a ScalarCmd to ALL scalar features of a device at once.
        /// scalar : 0.0 – 1.0
        /// </summary>
        public void SendScalarCmdAll(int deviceIndex, float scalar, List<DeviceFeature> features)
        {
            if (!IsConnected || features == null || features.Count == 0) return;

            scalar = Mathf.Clamp01(scalar);

            var sb = new StringBuilder();
            sb.AppendFormat("\"DeviceIndex\":{0},\"Scalars\":[", deviceIndex);
            for (int i = 0; i < features.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{{\"Index\":{0},\"Scalar\":{1:F4},\"ActuatorType\":\"{2}\"}}",
                    features[i].Index, scalar, features[i].ActuatorType);
            }
            sb.Append(']');

            SendMsg("ScalarCmd", sb.ToString());
        }

        /// <summary>
        /// Send a RotateCmd.
        /// speed : 0.0 – 1.0,  clockwise : rotation direction
        /// </summary>
        public void SendRotateCmd(int deviceIndex, int featureIndex, float speed, bool clockwise)
        {
            if (!IsConnected) return;

            speed = Mathf.Clamp01(speed);

            string body = string.Format(CultureInfo.InvariantCulture,
                "\"DeviceIndex\":{0},\"Rotations\":[{{\"Index\":{1},\"Speed\":{2:F4},\"Clockwise\":{3}}}]",
                deviceIndex, featureIndex, speed, clockwise ? "true" : "false");

            SendMsg("RotateCmd", body);
        }

        /// <summary>
        /// Send a RotateCmd to ALL rotate features.
        /// </summary>
        public void SendRotateCmdAll(int deviceIndex, float speed, bool clockwise, List<DeviceFeature> features)
        {
            if (!IsConnected || features == null || features.Count == 0) return;

            speed = Mathf.Clamp01(speed);

            var sb = new StringBuilder();
            sb.AppendFormat("\"DeviceIndex\":{0},\"Rotations\":[", deviceIndex);
            for (int i = 0; i < features.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{{\"Index\":{0},\"Speed\":{1:F4},\"Clockwise\":{2}}}",
                    features[i].Index, speed, clockwise ? "true" : "false");
            }
            sb.Append(']');

            SendMsg("RotateCmd", sb.ToString());
        }

        /// <summary>
        /// Stop all device movement.
        /// </summary>
        public void StopDevice(int deviceIndex)
        {
            if (!IsConnected) return;
            SendMsg("StopDeviceCmd",
                string.Format("\"DeviceIndex\":{0}", deviceIndex));
        }

        // ============================================================
        //  Frame update – call from Unity Update()
        // ============================================================

        public void Update(float deltaTime)
        {
            if (_ws == null) return;

            // Surface errors
            string err;
            while ((err = _ws.GetNextError()) != null)
            {
                RaiseError(err);
                SetStatus("Error");
            }

            // Detect unexpected disconnect
            if (!_ws.IsConnected && _handshakeComplete)
            {
                _handshakeComplete = false;
                SetStatus("Disconnected (connection lost)");
                return;
            }

            // Process received Buttplug messages
            string raw;
            while ((raw = _ws.GetNextMessage()) != null)
            {
                ProcessRawMessage(raw);
            }

            // Keepalive ping
            if (_pingInterval > 0 && _handshakeComplete)
            {
                _pingTimer -= deltaTime;
                if (_pingTimer <= 0)
                {
                    SendMsg("Ping", "");
                    _pingTimer = _pingInterval;
                }
            }
        }

        // ============================================================
        //  Internal helpers
        // ============================================================

        private void SendMsg(string type, string fields)
        {
            int id = _nextMsgId++;
            string json;
            if (string.IsNullOrEmpty(fields))
                json = "[{\"" + type + "\":{\"Id\":" + id + "}}]";
            else
                json = "[{\"" + type + "\":{\"Id\":" + id + "," + fields + "}}]";

            _ws.Send(json);
        }

        private void ProcessRawMessage(string rawJson)
        {
            var messages = MiniJsonParser.Parse(rawJson) as List<object>;
            if (messages == null) return;

            foreach (var msgObj in messages)
            {
                var msgDict = msgObj as Dictionary<string, object>;
                if (msgDict == null) continue;

                foreach (var kvp in msgDict)
                {
                    var body = kvp.Value as Dictionary<string, object>;
                    if (body == null) continue;
                    HandleMessage(kvp.Key, body);
                }
            }
        }

        private void HandleMessage(string type, Dictionary<string, object> body)
        {
            switch (type)
            {
                case "ServerInfo":
                    HandleServerInfo(body);
                    break;
                case "DeviceList":
                    HandleDeviceList(body);
                    break;
                case "DeviceAdded":
                    HandleDeviceAdded(body);
                    break;
                case "DeviceRemoved":
                    HandleDeviceRemoved(body);
                    break;
                case "ScanningFinished":
                    break;
                case "Ok":
                    break;
                case "Error":
                    HandleError(body);
                    break;
            }
        }

        private void HandleServerInfo(Dictionary<string, object> body)
        {
            _handshakeComplete = true;

            double maxPingTime = GetDouble(body, "MaxPingTime", 0);
            if (maxPingTime > 0)
            {
                _pingInterval = (float)(maxPingTime / 1000.0 * 0.4);
                _pingTimer = _pingInterval;
            }

            SetStatus("Connected");

            // Auto-discover devices
            RequestDeviceList();
            StartScanning();
        }

        private void HandleDeviceList(Dictionary<string, object> body)
        {
            Devices.Clear();
            var list = GetList(body, "Devices");
            if (list != null)
            {
                foreach (var obj in list)
                {
                    var d = obj as Dictionary<string, object>;
                    if (d != null) ParseAndAddDevice(d);
                }
            }

            int linearCount = 0;
            foreach (var dev in Devices)
                if (dev.SupportsLinearCmd) linearCount++;

            // Build a summary string
            var summary = new StringBuilder();
            summary.Append("Connected \u2013 ");
            summary.Append(Devices.Count);
            summary.Append(" device(s)");
            foreach (var dev in Devices)
            {
                summary.Append("\n  ");
                summary.Append(dev.GetLabel());
                summary.Append(": ");
                summary.Append(dev.GetCapabilitySummary());
            }
            SetStatus(summary.ToString());

            if (OnDevicesChanged != null) OnDevicesChanged();
        }

        private void HandleDeviceAdded(Dictionary<string, object> body)
        {
            ParseAndAddDevice(body);

            // Log detailed feature info for debugging
            var dev = Devices[Devices.Count - 1];
            var log = new StringBuilder();
            log.Append("VAM Launch: Device added: ");
            log.Append(dev.GetLabel());
            log.Append(" | ");
            log.Append(dev.GetCapabilitySummary());
            log.Append(" | CanRotate=");
            log.Append(dev.CanRotate);
            log.Append(" RotateCmd=");
            log.Append(dev.SupportsRotateCmd);
            log.Append(" RotateScalar=");
            log.Append(dev.HasRotateScalarFeature);
            foreach (var f in dev.ScalarFeatures)
            {
                log.Append("\n    Scalar[");
                log.Append(f.Index);
                log.Append("]: ");
                log.Append(f.ActuatorType);
                log.Append(" (");
                log.Append(f.FeatureDescriptor);
                log.Append(") steps=");
                log.Append(f.StepCount);
            }
            foreach (var f in dev.RotateFeatures)
            {
                log.Append("\n    Rotate[");
                log.Append(f.Index);
                log.Append("]: ");
                log.Append(f.ActuatorType);
                log.Append(" (");
                log.Append(f.FeatureDescriptor);
                log.Append(") steps=");
                log.Append(f.StepCount);
            }
            UnityEngine.Debug.Log(log.ToString());

            SetStatus("Connected \u2013 device added (" + Devices.Count + " total)");
            if (OnDevicesChanged != null) OnDevicesChanged();
        }

        private void HandleDeviceRemoved(Dictionary<string, object> body)
        {
            int idx = (int)GetDouble(body, "DeviceIndex", -1);
            Devices.RemoveAll(d => d.DeviceIndex == idx);
            SetStatus("Connected – " + Devices.Count + " device(s)");
            if (OnDevicesChanged != null) OnDevicesChanged();
        }

        private void HandleError(Dictionary<string, object> body)
        {
            string msg = GetString(body, "ErrorMessage", "Unknown error");
            int code = (int)GetDouble(body, "ErrorCode", 0);
            RaiseError("Buttplug [" + code + "]: " + msg);
        }

        // ---- device parsing ----

        private void ParseAndAddDevice(Dictionary<string, object> data)
        {
            var dev = new ButtplugDevice();
            dev.DeviceIndex = (int)GetDouble(data, "DeviceIndex", 0);
            dev.DeviceName = GetString(data, "DeviceName", "Unknown");
            dev.DisplayName = GetString(data, "DeviceDisplayName", "");

            var msgs = GetDict(data, "DeviceMessages");
            if (msgs != null)
            {
                // Buttplug v3: each command type maps to an ARRAY of feature descriptors
                ParseFeatureArray(msgs, "LinearCmd", dev.LinearFeatures);
                ParseFeatureArray(msgs, "ScalarCmd", dev.ScalarFeatures);
                ParseFeatureArray(msgs, "RotateCmd", dev.RotateFeatures);
            }

            Devices.RemoveAll(d => d.DeviceIndex == dev.DeviceIndex);
            Devices.Add(dev);
        }

        /// <summary>
        /// Parse a Buttplug v3 feature array.
        /// Format: "CmdType": [ {"FeatureDescriptor":"...","ActuatorType":"...","StepCount":N}, ... ]
        /// Older servers may send a dict with FeatureCount/StepCount[] instead.
        /// </summary>
        private void ParseFeatureArray(Dictionary<string, object> msgs, string key, List<DeviceFeature> outList)
        {
            outList.Clear();
            object raw;
            if (!msgs.TryGetValue(key, out raw)) return;

            // v3 format: array of feature descriptors
            var arr = raw as List<object>;
            if (arr != null)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var fd = arr[i] as Dictionary<string, object>;
                    if (fd == null) continue;

                    var feat = new DeviceFeature();
                    feat.Index = (int)GetDouble(fd, "Index", i);
                    feat.FeatureDescriptor = GetString(fd, "FeatureDescriptor", "");
                    feat.ActuatorType = GetString(fd, "ActuatorType", feat.FeatureDescriptor);
                    feat.StepCount = (int)GetDouble(fd, "StepCount", 20);
                    outList.Add(feat);
                }
                return;
            }

            // Fallback: older dict format  {"FeatureCount":N, "StepCount":[...]}
            var dict = raw as Dictionary<string, object>;
            if (dict != null)
            {
                int count = (int)GetDouble(dict, "FeatureCount", 1);
                var stepList = GetList(dict, "StepCount");
                for (int i = 0; i < count; i++)
                {
                    var feat = new DeviceFeature();
                    feat.Index = i;
                    feat.FeatureDescriptor = key.Replace("Cmd", "");
                    feat.ActuatorType = feat.FeatureDescriptor;
                    feat.StepCount = (stepList != null && i < stepList.Count && stepList[i] is double)
                        ? (int)(double)stepList[i] : 20;
                    outList.Add(feat);
                }
            }
        }

        // ---- status / error helpers ----

        private void SetStatus(string s)
        {
            Status = s;
            if (OnStatusChanged != null) OnStatusChanged(s);
        }

        private void RaiseError(string e)
        {
            if (OnError != null) OnError(e);
        }

        // ---- JSON value helpers ----

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is double) return (double)v;
            return def;
        }

        private static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is string) return (string)v;
            return def;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        private static List<object> GetList(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v)) return v as List<object>;
            return null;
        }
    }

    // ================================================================
    //  Minimal JSON parser  (Dictionary / List / string / double / bool / null)
    //  Sufficient for Buttplug protocol message parsing.
    // ================================================================
    internal static class MiniJsonParser
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int idx = 0;
            return ParseValue(json, ref idx);
        }

        private static object ParseValue(string json, ref int i)
        {
            SkipWS(json, ref i);
            if (i >= json.Length) return null;
            char c = json[i];
            if (c == '{') return ParseObject(json, ref i);
            if (c == '[') return ParseArray(json, ref i);
            if (c == '"') return ParseString(json, ref i);
            if (c == 't' || c == 'f') return ParseBool(json, ref i);
            if (c == 'n') { i += 4; return null; }
            return ParseNumber(json, ref i);
        }

        private static Dictionary<string, object> ParseObject(string json, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // '{'
            SkipWS(json, ref i);
            if (i < json.Length && json[i] == '}') { i++; return d; }

            while (i < json.Length)
            {
                SkipWS(json, ref i);
                string key = ParseString(json, ref i);
                SkipWS(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                object val = ParseValue(json, ref i);
                d[key] = val;
                SkipWS(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break;
            }
            if (i < json.Length && json[i] == '}') i++;
            return d;
        }

        private static List<object> ParseArray(string json, ref int i)
        {
            var list = new List<object>();
            i++; // '['
            SkipWS(json, ref i);
            if (i < json.Length && json[i] == ']') { i++; return list; }

            while (i < json.Length)
            {
                list.Add(ParseValue(json, ref i));
                SkipWS(json, ref i);
                if (i < json.Length && json[i] == ',') { i++; continue; }
                break;
            }
            if (i < json.Length && json[i] == ']') i++;
            return list;
        }

        private static string ParseString(string json, ref int i)
        {
            i++; // opening '"'
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && i < json.Length)
                {
                    c = json[i++];
                    switch (c)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 <= json.Length)
                            {
                                string hex = json.Substring(i, 4);
                                int codePoint;
                                if (int.TryParse(hex, NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out codePoint))
                                    sb.Append((char)codePoint);
                                i += 4;
                            }
                            break;
                        default: sb.Append(c); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static double ParseNumber(string json, ref int i)
        {
            int start = i;
            while (i < json.Length && "0123456789+-.eE".IndexOf(json[i]) >= 0) i++;
            string s = json.Substring(start, i - start);
            double result;
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            return result;
        }

        private static bool ParseBool(string json, ref int i)
        {
            if (json[i] == 't') { i += 4; return true; }
            i += 5;
            return false;
        }

        private static void SkipWS(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }
    }
}
