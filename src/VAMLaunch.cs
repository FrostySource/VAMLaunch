using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VAMLaunchPlugin.MotionSources;

namespace VAMLaunchPlugin
{
    public class VAMLaunch : MVRScript
    {
        private static VAMLaunch _instance;
        
        // Legacy UDP settings
        private const string SERVER_IP = "127.0.0.1";
        private const int SERVER_LISTEN_PORT = 15600;
        private const int SERVER_SEND_PORT = 15601;
        private const float NETWORK_LISTEN_INTERVAL = 0.033f;
        
        private VAMLaunchNetwork _network;
        private float _networkPollTimer;

        private byte _lastSentLaunchPos;

        // Connection mode
        private const string CONNECTION_MODE_LEGACY = "Legacy UDP";
        private const string CONNECTION_MODE_INTIFACE = "Intiface/JoyHub";

        private IntifaceClient _intifaceClient;
        private bool _useIntiface;
        private int _selectedDeviceIndex = -1;

        private JSONStorableStringChooser _connectionModeChooser;
        private JSONStorableFloat _intifacePort;
        private JSONStorableString _connectionStatus;
        private JSONStorableStringChooser _deviceChooser;

        // --- Rotate (telescoping spin) – sent with each motion command ---
        private JSONStorableBool _rotateEnabled;
        private JSONStorableFloat _rotateMin;        // spin speed at slowest motion
        private JSONStorableFloat _rotateMax;        // spin speed at fastest motion
        private JSONStorableBool _rotateClockwise;
        private JSONStorableBool _rotateAlternate;   // flip direction on motion reversal
        private bool _rotateCurrentCW = true;
        private bool _lastMotionDirectionUp = true;  // for detecting reversals
        private float _lastSentRotateSpeed = -1f;

        // --- Constrict (vacuum pump) – 1-7 preset levels ---
        private JSONStorableBool _constrictEnabled;
        private JSONStorableFloat _constrictLevel;   // 1 – 7
        private float _lastSentConstrictVal = -1f;

        // Throttle interval to avoid flooding the device
        private const float FEATURE_SEND_INTERVAL = 0.05f;
        private float _featureSendTimer;

        private List<string> _connectionModeChoices = new List<string>
        {
            CONNECTION_MODE_LEGACY,
            CONNECTION_MODE_INTIFACE
        };

        private JSONStorableStringChooser _motionSourceChooser;
        private JSONStorableBool _pauseLaunchMessages;
        private JSONStorableFloat _simulatorPosition;
        
        private float _simulatorTarget;
        private float _simulatorSpeed;

        private IMotionSource _currentMotionSource;
        private int _currentMotionSourceIndex = -1;
        private int _desiredMotionSourceIndex;

        private List<string> _motionSourceChoices = new List<string>
        {
            "Oscillate",
            "Pattern",
            "Zone"
        };

        private List<IMotionSource> _motionSources = new List<IMotionSource>
        {
            new OscillateSource(),
            new PatternSource(),
            new ZoneSource()
        };
        
        public override void Init()
        {
            if (_instance != null)
            {
                SuperController.LogError("You can only have one instance of VAM Launch active!");
                return;
            }
            
            if (containingAtom == null || containingAtom.type == "CoreControl")
            {
                SuperController.LogError("Please add VAM Launch to in scene atom!");
                return;
            }

            _instance = this;

            InitStorables();
            InitOptionsUI();
            InitActions();
            InitNetwork();
        }

        private void InitNetwork()
        {
            if (_useIntiface)
            {
                _intifaceClient = new IntifaceClient();
                _intifaceClient.OnDevicesChanged = OnIntifaceDevicesChanged;
                _intifaceClient.OnStatusChanged = OnIntifaceStatusChanged;
                _intifaceClient.OnError = OnIntifaceError;
                _connectionStatus.SetVal("Ready – press Connect");
                SuperController.LogMessage("VAM Launch: Intiface/JoyHub mode. Press Connect to start.");
            }
            else
            {
                _network = new VAMLaunchNetwork();
                _network.Init(SERVER_IP, SERVER_LISTEN_PORT, SERVER_SEND_PORT);
                _connectionStatus.SetVal("Legacy UDP connected");
                SuperController.LogMessage("VAM Launch: Legacy UDP connection established.");
            }
        }
        
        private void InitStorables()
        {
            // --- Connection settings ---
            _connectionModeChooser = new JSONStorableStringChooser("connectionMode",
                _connectionModeChoices, CONNECTION_MODE_INTIFACE, "Connection Mode",
                (string mode) =>
                {
                    bool wantIntiface = mode == CONNECTION_MODE_INTIFACE;
                    if (wantIntiface != _useIntiface)
                    {
                        ShutdownNetwork();
                        _useIntiface = wantIntiface;
                        InitNetwork();
                    }
                });
            RegisterStringChooser(_connectionModeChooser);
            _useIntiface = _connectionModeChooser.val == CONNECTION_MODE_INTIFACE;

            _intifacePort = new JSONStorableFloat("intifacePort", 12345f, 1f, 65535f);
            RegisterFloat(_intifacePort);

            _connectionStatus = new JSONStorableString("connectionStatus", "Initialising…");

            _deviceChooser = new JSONStorableStringChooser("intifaceDevice",
                new List<string> { "None" }, "None", "Device",
                (string label) =>
                {
                    _selectedDeviceIndex = -1;
                    if (_intifaceClient == null) return;
                    foreach (var dev in _intifaceClient.Devices)
                    {
                        if (dev.GetLabel() == label)
                        {
                            _selectedDeviceIndex = dev.DeviceIndex;
                            break;
                        }
                    }
                });
            RegisterStringChooser(_deviceChooser);

            // --- Rotate (linked to motion source) storables ---
            _rotateEnabled = new JSONStorableBool("rotateEnabled", false);
            RegisterBool(_rotateEnabled);

            _rotateMin = new JSONStorableFloat("rotateMin", 0.0f, 0.0f, 1.0f);
            RegisterFloat(_rotateMin);

            _rotateMax = new JSONStorableFloat("rotateMax", 0.5f, 0.0f, 1.0f);
            RegisterFloat(_rotateMax);

            _rotateClockwise = new JSONStorableBool("rotateClockwise", true);
            RegisterBool(_rotateClockwise);

            _rotateAlternate = new JSONStorableBool("rotateAlternate", false);
            RegisterBool(_rotateAlternate);

            // --- Constrict (vacuum pump) 1-7 preset levels ---
            _constrictEnabled = new JSONStorableBool("constrictEnabled", false);
            RegisterBool(_constrictEnabled);

            _constrictLevel = new JSONStorableFloat("constrictLevel", 1f, 1f, 7f);
            RegisterFloat(_constrictLevel);

            // --- Existing storables ---
            _motionSourceChooser = new JSONStorableStringChooser("motionSource", _motionSourceChoices, "",
                "Motion Source",
                (string name) => { _desiredMotionSourceIndex = GetMotionSourceIndex(name); });
            _motionSourceChooser.choices = _motionSourceChoices;
            RegisterStringChooser(_motionSourceChooser);
            if (string.IsNullOrEmpty(_motionSourceChooser.val))
            {
                _motionSourceChooser.SetVal(_motionSourceChoices[0]);
            }
            
            _pauseLaunchMessages = new JSONStorableBool("pauseLaunchMessages", true);
            RegisterBool(_pauseLaunchMessages);
            
            _simulatorPosition = new JSONStorableFloat("simulatorPosition", 0.0f, 0.0f, LaunchUtils.LAUNCH_MAX_VAL);
            RegisterFloat(_simulatorPosition);

            foreach (var ms in _motionSources)
            {
                ms.OnInitStorables(this);
            }
        }
        
        private void InitOptionsUI()
        {
            // --- Connection UI (left column) ---
            CreateScrollablePopup(_connectionModeChooser);

            var portSlider = CreateSlider(_intifacePort);
            portSlider.label = "Intiface Port";
            portSlider.slider.wholeNumbers = true;

            var connectBtn = CreateButton("Connect to Intiface / JoyHub");
            connectBtn.button.onClick.AddListener(ConnectIntiface);

            var disconnectBtn = CreateButton("Disconnect");
            disconnectBtn.button.onClick.AddListener(DisconnectIntiface);

            var statusField = CreateTextField(_connectionStatus);
            statusField.height = 35f;

            CreateScrollablePopup(_deviceChooser);

            var scanBtn = CreateButton("Scan for Devices");
            scanBtn.button.onClick.AddListener(() =>
            {
                if (_intifaceClient != null && _intifaceClient.IsConnected)
                {
                    _intifaceClient.RequestDeviceList();
                    _intifaceClient.StartScanning();
                }
            });

            CreateSpacer();

            // --- Original controls ---
            var toggle = CreateToggle(_pauseLaunchMessages);
            toggle.label = "Pause Launch";
            
            var slider = CreateSlider(_simulatorPosition, false);
            slider.label = "Simulator";
            
            CreateScrollablePopup(_motionSourceChooser);

            CreateSpacer();

            // --- Rotate (linked to motion) UI (right column) ---
            var rotateToggle = CreateToggle(_rotateEnabled, true);
            rotateToggle.label = "Enable Rotate (Spin)";

            var rotateMinSlider = CreateSlider(_rotateMin, true);
            rotateMinSlider.label = "Rotate Min Speed";
            rotateMinSlider.slider.onValueChanged.AddListener((v) =>
            {
                _rotateMin.SetVal(Mathf.Min(v, _rotateMax.val));
            });

            var rotateMaxSlider = CreateSlider(_rotateMax, true);
            rotateMaxSlider.label = "Rotate Max Speed";
            rotateMaxSlider.slider.onValueChanged.AddListener((v) =>
            {
                _rotateMax.SetVal(Mathf.Max(v, _rotateMin.val));
            });

            var rotateDirToggle = CreateToggle(_rotateClockwise, true);
            rotateDirToggle.label = "Clockwise";

            var rotateAltToggle = CreateToggle(_rotateAlternate, true);
            rotateAltToggle.label = "Alternate on Reversal";

            CreateSpacer(true);

            // --- Constrict (vacuum pump) UI (right column) ---
            var constrictToggle = CreateToggle(_constrictEnabled, true);
            constrictToggle.label = "Enable Constrict (Vacuum)";

            var constrictSlider = CreateSlider(_constrictLevel, true);
            constrictSlider.label = "Suction Level (1-7)";
            constrictSlider.slider.wholeNumbers = true;

            CreateSpacer(true);
        }

        private void InitActions()
        {
            JSONStorableAction startLaunchAction = new JSONStorableAction("startLaunch", () =>
            {
                _pauseLaunchMessages.SetVal(false);
            });
            RegisterAction(startLaunchAction);
            
            JSONStorableAction stopLaunchAction = new JSONStorableAction("stopLaunch", () =>
            {
                _pauseLaunchMessages.SetVal(true);
            });
            RegisterAction(stopLaunchAction);
            
            JSONStorableAction toggleLaunchAction = new JSONStorableAction("toggleLaunch", () =>
            {
                _pauseLaunchMessages.SetVal(!_pauseLaunchMessages.val);
            });
            RegisterAction(toggleLaunchAction);

            JSONStorableAction connectIntifaceAction = new JSONStorableAction("connectIntiface", ConnectIntiface);
            RegisterAction(connectIntifaceAction);

            JSONStorableAction disconnectIntifaceAction = new JSONStorableAction("disconnectIntiface", DisconnectIntiface);
            RegisterAction(disconnectIntifaceAction);
        }

        // ============================================================
        //  Intiface / JoyHub  connection helpers
        // ============================================================

        private void ConnectIntiface()
        {
            if (!_useIntiface)
            {
                SuperController.LogMessage("VAM Launch: Switch to Intiface/JoyHub mode first.");
                return;
            }

            if (_intifaceClient == null)
            {
                _intifaceClient = new IntifaceClient();
                _intifaceClient.OnDevicesChanged = OnIntifaceDevicesChanged;
                _intifaceClient.OnStatusChanged = OnIntifaceStatusChanged;
                _intifaceClient.OnError = OnIntifaceError;
            }

            int port = (int)_intifacePort.val;
            _intifaceClient.Connect("127.0.0.1", port);
        }

        private void DisconnectIntiface()
        {
            if (_intifaceClient != null)
            {
                _intifaceClient.Disconnect();
            }
            _selectedDeviceIndex = -1;
        }

        private void OnIntifaceDevicesChanged()
        {
            var choices = new List<string>();
            choices.Add("None");

            int firstDeviceIdx = -1;
            string firstDeviceLabel = null;

            foreach (var device in _intifaceClient.Devices)
            {
                string label = device.GetLabel();
                choices.Add(label);

                // Remember the first device that has any usable feature
                if (device.HasAnyFeature && firstDeviceIdx < 0)
                {
                    firstDeviceIdx = device.DeviceIndex;
                    firstDeviceLabel = label;
                }
            }

            _deviceChooser.choices = choices;

            // Auto-select the first capable device when nothing is selected
            if (_selectedDeviceIndex < 0 && firstDeviceIdx >= 0)
            {
                _deviceChooser.SetVal(firstDeviceLabel);
                _selectedDeviceIndex = firstDeviceIdx;
            }
        }

        private void OnIntifaceStatusChanged(string status)
        {
            if (_connectionStatus != null)
            {
                _connectionStatus.SetVal(status);
            }
        }

        private void OnIntifaceError(string error)
        {
            SuperController.LogError("VAM Launch: " + error);
        }

        private int GetMotionSourceIndex(string name)
        {
            return _motionSourceChoices.IndexOf(name);
        }
        
        private void UpdateMotionSource()
        {
            if (_desiredMotionSourceIndex != _currentMotionSourceIndex)
            {
                if (_currentMotionSource != null)
                {
                    _currentMotionSource.OnDestroy(this);
                    _currentMotionSource = null;
                }

                if (_desiredMotionSourceIndex >= 0)
                {
                    _currentMotionSource = _motionSources[_desiredMotionSourceIndex];
                    _currentMotionSource.OnInit(this);
                }

                _currentMotionSourceIndex = _desiredMotionSourceIndex;
            }

            if (_currentMotionSource != null)
            {
                byte pos = 0;
                byte speed = 0;
                if (_currentMotionSource.OnUpdate(ref pos, ref speed))
                {
                    SendLaunchPosition(pos, speed);
                }
            }
        }
        
        private void OnDestroy()
        {
            ShutdownNetwork();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void ShutdownNetwork()
        {
            if (_network != null)
            {
                SuperController.LogMessage("VAM Launch: Shutting down Legacy UDP.");
                _network.Stop();
                _network = null;
            }

            if (_intifaceClient != null)
            {
                SuperController.LogMessage("VAM Launch: Shutting down Intiface connection.");
                _intifaceClient.Disconnect();
                _intifaceClient = null;
            }

            _lastSentRotateSpeed = -1f;
            _lastSentConstrictVal = -1f;
        }

        private void Update()
        {
            UpdateMotionSource();
            UpdateSimulator();

            if (_useIntiface)
            {
                if (_intifaceClient != null)
                {
                    _intifaceClient.Update(Time.deltaTime);
                    UpdateIndependentFeatures(Time.deltaTime);
                }
            }
            else
            {
                UpdateNetwork();
            }
        }

        // ============================================================
        //  Independent feature updates (Constrict only)
        // ============================================================

        private void UpdateIndependentFeatures(float deltaTime)
        {
            if (_intifaceClient == null || !_intifaceClient.IsConnected || _selectedDeviceIndex < 0)
                return;

            ButtplugDevice device = GetSelectedDevice();
            if (device == null) return;

            // Advance the shared throttle timer once per frame
            _featureSendTimer -= deltaTime;
            bool canSend = _featureSendTimer <= 0f;

            // --- Constrict (1-7 preset levels) ---
            if (device.SupportsScalarCmd)
            {
                float targetConstrict = 0f;
                if (_constrictEnabled.val)
                {
                    // Map level 1-7 to 0..1  (level 1 = ~0.14, level 7 = 1.0)
                    targetConstrict = Mathf.Clamp01(_constrictLevel.val / 7f);
                }

                if (canSend && Mathf.Abs(targetConstrict - _lastSentConstrictVal) > 0.005f)
                {
                    foreach (var feat in device.ScalarFeatures)
                    {
                        string at = feat.ActuatorType.ToLower();
                        if (at == "constrict" || at == "suction" || at == "pressure" || at == "inflate")
                        {
                            _intifaceClient.SendScalarCmd(
                                device.DeviceIndex, feat.Index, targetConstrict, feat.ActuatorType);
                        }
                    }
                    _lastSentConstrictVal = targetConstrict;
                }
            }

            // Reset throttle timer
            if (canSend)
                _featureSendTimer = FEATURE_SEND_INTERVAL;
        }

        private ButtplugDevice GetSelectedDevice()
        {
            if (_intifaceClient == null) return null;
            foreach (var dev in _intifaceClient.Devices)
            {
                if (dev.DeviceIndex == _selectedDeviceIndex)
                    return dev;
            }
            return null;
        }

        private void UpdateSimulator()
        {
            var prevPos = _simulatorPosition.val;

            var newPos = Mathf.MoveTowards(prevPos, _simulatorTarget,
                LaunchUtils.PredictDistanceTraveled(_simulatorSpeed, Time.deltaTime));
            
            _simulatorPosition.SetVal(newPos);

            if (_currentMotionSource != null)
            {
                _currentMotionSource.OnSimulatorUpdate(prevPos, newPos, Time.deltaTime);
            }
        }

        private void SetSimulatorTarget(float pos, float speed)
        {
            _simulatorTarget = Mathf.Clamp(pos, 0.0f, LaunchUtils.LAUNCH_MAX_VAL);
            _simulatorSpeed = Mathf.Clamp(speed, 0.0f, LaunchUtils.LAUNCH_MAX_VAL);
        }
        
        // Not really used yet, but there just incase we want to do two way communication between server
        private void UpdateNetwork()
        {
            if (_network == null)
            {
                return;
            }

            _networkPollTimer -= Time.deltaTime;
            if (_networkPollTimer <= 0.0f)
            {
                ReceiveNetworkMessages();
                _networkPollTimer = NETWORK_LISTEN_INTERVAL - Mathf.Min(-_networkPollTimer, NETWORK_LISTEN_INTERVAL);
            }
        }

        private void ReceiveNetworkMessages()
        {
            byte[] msg = _network.GetNextMessage();
            if (msg != null && msg.Length > 0)
            {
                //SuperController.LogMessage(msg[0].ToString());
            }
        }

        
        private static byte[] _launchData = new byte[6];
        private void SendLaunchPosition(byte pos, byte speed)
        {
            SetSimulatorTarget(pos, speed);

            if (_pauseLaunchMessages.val)
            {
                // Stop rotate motor when pausing
                if (_lastSentRotateSpeed > 0f && _useIntiface
                    && _intifaceClient != null && _intifaceClient.IsConnected && _selectedDeviceIndex >= 0)
                {
                    ButtplugDevice device = GetSelectedDevice();
                    if (device != null)
                    {
                        if (device.SupportsRotateCmd)
                        {
                            _intifaceClient.SendRotateCmdAll(
                                device.DeviceIndex, 0f, true, device.RotateFeatures);
                        }
                        if (device.HasRotateScalarFeature)
                        {
                            foreach (var feat in device.ScalarFeatures)
                            {
                                if (feat.ActuatorType.ToLower() == "rotate")
                                {
                                    _intifaceClient.SendScalarCmd(
                                        device.DeviceIndex, feat.Index, 0f, feat.ActuatorType);
                                }
                            }
                        }
                        _lastSentRotateSpeed = 0f;
                    }
                }
                return;
            }

            float dist = Mathf.Abs(pos - _lastSentLaunchPos);
            float duration = LaunchUtils.PredictMoveDuration(dist, speed);

            if (_useIntiface)
            {
                if (_intifaceClient != null && _intifaceClient.IsConnected && _selectedDeviceIndex >= 0)
                {
                    ButtplugDevice device = GetSelectedDevice();
                    if (device != null)
                    {
                        // Linear (position-based stroke)
                        if (device.SupportsLinearCmd)
                        {
                            float normalizedPos = pos / LaunchUtils.LAUNCH_MAX_VAL;
                            int durationMs = Mathf.Max((int)(duration * 1000f), 20);
                            _intifaceClient.SendLinearCmd(device.DeviceIndex, normalizedPos, durationMs);
                        }

                        // Rotate – tied to the motion source speed
                        if (device.CanRotate && _rotateEnabled.val)
                        {
                            // Normalise speed (motion sources use ~20-80 range)
                            float normalizedSpeed = Mathf.Clamp01(
                                (speed - LaunchUtils.LAUNCH_MIN_SPEED) /
                                (LaunchUtils.LAUNCH_MAX_SPEED - LaunchUtils.LAUNCH_MIN_SPEED));
                            float rotateSpeed = Mathf.Lerp(_rotateMin.val, _rotateMax.val, normalizedSpeed);

                            // Alternation: detect motion direction change
                            bool goingUp = pos > _lastSentLaunchPos;
                            if (_rotateAlternate.val && goingUp != _lastMotionDirectionUp)
                            {
                                _rotateCurrentCW = !_rotateCurrentCW;
                            }
                            _lastMotionDirectionUp = goingUp;

                            bool cw = _rotateAlternate.val ? _rotateCurrentCW : _rotateClockwise.val;

                            // Send via RotateCmd if device has dedicated rotate features
                            if (device.SupportsRotateCmd)
                            {
                                _intifaceClient.SendRotateCmdAll(
                                    device.DeviceIndex, rotateSpeed, cw, device.RotateFeatures);
                            }

                            // Also send via ScalarCmd for devices that expose rotate as a scalar (0-1 maps to 0-255)
                            if (device.HasRotateScalarFeature)
                            {
                                foreach (var feat in device.ScalarFeatures)
                                {
                                    if (feat.ActuatorType.ToLower() == "rotate")
                                    {
                                        _intifaceClient.SendScalarCmd(
                                            device.DeviceIndex, feat.Index, rotateSpeed, feat.ActuatorType);
                                    }
                                }
                            }

                            _lastSentRotateSpeed = rotateSpeed;
                        }
                        else if (device.CanRotate && !_rotateEnabled.val
                                 && _lastSentRotateSpeed > 0f)
                        {
                            // Send stop when disabled
                            if (device.SupportsRotateCmd)
                            {
                                _intifaceClient.SendRotateCmdAll(
                                    device.DeviceIndex, 0f, true, device.RotateFeatures);
                            }
                            if (device.HasRotateScalarFeature)
                            {
                                foreach (var feat in device.ScalarFeatures)
                                {
                                    if (feat.ActuatorType.ToLower() == "rotate")
                                    {
                                        _intifaceClient.SendScalarCmd(
                                            device.DeviceIndex, feat.Index, 0f, feat.ActuatorType);
                                    }
                                }
                            }
                            _lastSentRotateSpeed = 0f;
                        }
                    }
                }
            }
            else
            {
                // Legacy UDP path
                if (_network != null)
                {
                    _launchData[0] = pos;
                    _launchData[1] = speed;

                    var durationData = BitConverter.GetBytes(duration);
                    _launchData[2] = durationData[0];
                    _launchData[3] = durationData[1];
                    _launchData[4] = durationData[2];
                    _launchData[5] = durationData[3];

                    _network.Send(_launchData, _launchData.Length);
                }
            }

            _lastSentLaunchPos = pos;
        }
    }
}