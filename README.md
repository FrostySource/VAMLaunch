# VAMLaunch – Intiface / JoyHub Edition

A VaM (Virt-A-Mate) plugin that controls interactive devices through **Intiface Central** or **JoyHub** using the Buttplug v3 protocol over WebSocket.

## Features

### Direct Intiface / JoyHub Connection
- Connects directly to Intiface Central or JoyHub via WebSocket (no external bridge application needed)
- Automatic device discovery and scanning
- Full Buttplug v3 protocol support with device capability detection
- Legacy UDP mode still available for backwards compatibility

### Supported Device Functions

**Linear (Stroke)**
Devices with position-based movement (e.g. Launch, OSR) receive `LinearCmd` messages driven by the selected motion source.

**Rotate (Telescoping Spin)**
Rotation speed is tied to the active motion source — faster strokes produce faster spin. Works with both dedicated `RotateCmd` devices and devices that expose rotation as a `ScalarCmd` (0–255 range).

- **Min / Max Speed** — maps motion source speed to a spin speed range
- **Clockwise** — base rotation direction
- **Alternate on Reversal** — flips direction each time the stroke changes direction

**Constrict (Vacuum / Suction)**
Simple 1–7 level control for vacuum pump style devices. Sends `ScalarCmd` to constrict, suction, pressure, and inflate actuator types.

### Motion Sources
Three built-in motion sources control the stroke pattern:

| Source | Description |
|--------|-------------|
| **Oscillate** | Bounce between min/max position at a set speed, optionally linked to an animation pattern |
| **Pattern** | Follow an animation pattern's control points directly |
| **Zone** | Zone-based tracking with velocity analysis |

## Installation

1. Copy the `ADD_ME.cslist` and `src/` folder into your VaM `Custom/Scripts/VAMLaunch/` directory
2. In VaM, add the plugin to a scene atom via **Add Plugin** → select `ADD_ME.cslist`

## Usage

1. Start **Intiface Central** or **JoyHub** on your machine
2. In the plugin UI, set the connection mode to **Intiface/JoyHub** and enter the WebSocket port (default: 12345)
3. Click **Connect to Intiface / JoyHub**
4. Click **Scan for Devices** — your device will appear in the device dropdown
5. Select a motion source and uncheck **Pause Launch** to begin
6. Enable **Rotate** and/or **Constrict** controls in the right panel as needed

## Connection Modes

| Mode | Protocol | Description |
|------|----------|-------------|
| **Intiface/JoyHub** | Buttplug v3 over WebSocket | Recommended. Direct connection to Intiface Central or JoyHub |
| **Legacy UDP** | Custom UDP | Original protocol for use with the VAMLaunch server bridge |

## Requirements

- VaM (Virt-A-Mate)
- Intiface Central or JoyHub running locally
- A Buttplug-compatible device

## License

BSD 3-Clause License — see [LICENSE](LICENSE) for full details.
