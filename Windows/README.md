# AimMac Windows receiver

This is the first Windows host for the existing iPhone app. It speaks the Bluetooth protocol already documented in `Shared/Protocol.swift`. The iPhone and Mac projects are unchanged.

The receiver is a 64-bit window. It does not move the cursor until three things are true:

1. You clicked **Start**.
2. You clicked **Approve** for the connected phone.
3. You clicked **Arm input**.

Arming is forgotten every time the app stops, closes, or changes mode. A device that has not been approved cannot move the cursor.

## Security limits

Read this before arming input. Nothing below was tested against a live radio, an iPhone, or the BARROT adapter.

**Secure mode is the default.** The GATT characteristics are published with `EncryptionAndAuthenticationRequired`. The intended gate is Windows BLE authenticated pairing (LE Secure Connections), which this PC's adapter reports as supported. The hello UUID from the phone is only a label. A saved approval is the Windows Bluetooth device id, and only after you click Approve. If Windows cannot supply that id, or the link was not started in secure mode, the device is denied.

This build does not prove that Windows actually refuses an unencrypted write, that the iPhone completes pairing, or that the pairing dialog is numeric comparison rather than Just Works. If pairing does not happen, leave input disarmed.

**Prototype mode is explicit and off by default.** It publishes the same characteristics in plaintext so the current iPhone app can connect the way it connects to a Mac. Turning it on requires a warning whose default button is No. Arming input in that mode requires a second warning, also defaulting to No. Each approval requires another confirmation. Prototype approvals are not saved. The hello UUID is not a credential: any nearby device that can transmit can present one. Prototype mode is a temporary way to prove motion, not a finished trust model.

There is no application session key. Adding one would mean a small protocol change on the phone (a nonce in a new field the current status packet does not have, and a response the phone does not send). That is not in this milestone.

Stop, Disarm, Pause, Deny, Revoke, disconnect, a link error, closing the window, and an unhandled UI exception all ask the pointer code to release a mouse button **if this app pressed it**. Killing the process from Task Manager can still leave the button down; the next test should click once with the real mouse if a drag is interrupted that way.

## What this milestone does not do

- It does not add the multi-host picker on the iPhone.
- It does not change the Mac app.
- It does not remember more than one approved secure identity as an active controller. Approving a second device returns the first to pending and releases the button.
- The advertised Bluetooth name is the Windows radio name, not a string this window can set. Legacy advertisements on the reported adapter are 31 bytes. A 128-bit service UUID uses most of that. Set the PC's Bluetooth radio name to a short unique name such as `Ironfist` before expecting the iPhone list to show it.

## Build

Requires the .NET 8 SDK. The app targets `net8.0-windows10.0.19041.0`, x64. Windows 10 build 15063 or later is the API floor for the GATT server. Windows 11 Pro build 26300 is past that.

On Windows, from this `Windows` directory:

```powershell
dotnet test .\AimMac.Windows.sln -c Release
dotnet publish .\src\AimMac.Receiver\AimMac.Receiver.csproj -c Release -r win-x64 --self-contained true -o .\publish
.\publish\AimMacReceiver.exe
```

`dotnet test` does not need Windows. The publish output is self-contained and was not committed. The executable has to be launched in the interactive user session. A service in session 0 cannot inject into the desktop.

An unpackaged exe does not need an MSIX `bluetooth` capability. Do not run it elevated.

A Linux or macOS machine can run the tests and can cross-compile the window, but it cannot execute the Bluetooth stack or the Win32 cursor. A successful publish is not a radio test.

## iPhone app

Use the existing Xcode project. No protocol change is required for this milestone.

- Xcode 15 or newer, with an SDK that matches the phone's iOS.
- Deployment target iOS 17 (`Apple/project.yml`).
- `brew install xcodegen`, then `cd Apple && xcodegen`.
- Signing is automatic. Copy `Apple/Config.xcconfig` to `Apple/Local.xcconfig` and set `BUNDLE_ID_PREFIX` to a prefix you own and `DEVELOPMENT_TEAM` to your Team ID. The checked-in prefix is `com.example`, which will not install on a device.
- A free Apple ID works. The app stops opening after 7 days; run it again from Xcode. Turn on Developer Mode and trust the developer profile.
- Privacy strings for Bluetooth and motion are already in the target. The phone app is foreground-only. Keep it on screen while aiming.
- On the phone, leave any previously paired Mac unpaired if you want the picker. Choose the Windows machine's advertised name, then approve it on the PC before arming input.

## Manual test on Ironfist

Do this on the PC. Do not treat a prototype-mode success as authentication.

1. Confirm the Bluetooth radio name is a short unique name. Note `MaxAdvertisementDataLength` in the window. The expected legacy value is 31, with advertisement offload off.
2. Start in **secure mode**. Leave input disarmed. Open AimMac on the iPhone and connect.
3. Record whether Windows or the iPhone shows a pairing prompt, whether the phone reaches "Allow on your Mac", and whether Approve becomes available. If the write never arrives, stop and switch to prototype mode. That result is data, not a failure of the warning.
4. If you use prototype mode, accept the warning, approve the phone, and only then arm input. Confirm the cursor stays still before Arm.
5. Point the top of the phone at the middle of the main display and tap Center. Move slowly. Note whether the cursor follows and whether the "aims in the last second" counter is near the phone's 100 Hz send rate or much lower. The counter is arrival rate, not end-to-end latency. Latency is what you see between the phone and the cursor; write it down in plain language.
6. Tap, then press and hold long enough to drag, then release. Pause, deny, revoke, stop, disconnect Bluetooth, and quit the app. After each one, click with the PC's real mouse and confirm the button is not stuck.
7. While one phone is approved and armed, a second connection must not move the cursor.
8. If the Samsung TV is mirroring the PC, the cursor on the TV is the PC cursor plus display lag. That lag is not the Bluetooth path.

Scroll and trackpad-relative move are implemented and covered by unit tests. Their Windows wheel sign was not checked on hardware. If scroll feels backwards, that mapping is the first place to look. It is not a reason to change the phone.

## Layout

- `src/AimMac.Core` is the packet codec, projection, and session gate. It has no WinRT dependency.
- `src/AimMac.Core.Tests` is the automated suite.
- `src/AimMac.Receiver` is the WinForms window, GATT server, and `SendInput` cursor.

License: the same MIT license as the rest of AimMac (`LICENSE` at the repository root).
