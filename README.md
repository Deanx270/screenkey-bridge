# ScreenKey Bridge

ScreenKey Bridge connects an Android phone to a Windows PC over an existing ADB connection. The first milestone captures the Windows virtual desktop on request and saves the PNG to the phone's gallery. Keyboard routing is **not implemented yet**.

## Requirements

- Windows 10 or 11, signed in to the interactive desktop; .NET 8 Desktop Runtime to run a framework-dependent build.
- Android 10 or later. ADB must already be authorized and connected, over USB or wireless.
- Android SDK platform-tools (`adb`) on the Windows PATH. Exactly one authorized ADB device should be connected.
- For development: .NET 8 SDK, JDK 17, Android SDK platform 35 and Gradle 8.9.

## Build and run

1. Build the companion: `dotnet build windows/ScreenKeyBridge/ScreenKeyBridge.csproj -c Release`.
2. Build the Android debug app: from `android`, run `gradle assembleDebug`, then install with `adb install -r app/build/outputs/apk/debug/app-debug.apk`.
3. Start `windows/ScreenKeyBridge/bin/Release/net8.0-windows/ScreenKeyBridge.exe` in your signed-in Windows session. It runs in the notification area. Right-click its icon and select **Show pairing key and status**.
4. Enter that key in the phone app and tap **Capture PC screen**. Images appear in `Pictures/ScreenKey Bridge` in the phone's gallery.

To produce a standalone Windows executable that does not require an installed .NET runtime, run `dotnet publish windows/ScreenKeyBridge/ScreenKeyBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish` and start `publish/ScreenKeyBridge.exe`.

The companion periodically checks ADB and sets up `adb reverse tcp:38473 tcp:38473`. If ADB is not on PATH, put platform-tools on PATH before starting the companion. If there are multiple devices, disconnect the others for now. The app captures the Windows virtual desktop, including all monitors, as one PNG; gaps between monitors are included. Capture while locked, protected content, and some display-sharing configurations may not work. No guarantee is made about visibility in third-party sharing or security tools.

The server binds only to Windows loopback and requires a random pairing key. The key is saved under your Windows user profile and in the Android app's private preferences; keep both devices and the key private. The tunnel is intended for a trusted ADB-connected device, not for exposing directly to a network. The Android app does not need broad storage access. Neither component sends screenshots to a cloud service.

## Current limitations

- No keyboard routing, PC shortcut, Android input method, startup-at-login option, or monitor selection yet.
- Pairing key rotation and selecting among multiple ADB devices are not yet exposed in the UI. To reset pairing, exit the companion, delete `%LOCALAPPDATA%\ScreenKeyBridge\pairing-key.txt`, and restart it; enter the new key on the phone.
- Wireless ADB connections depend on the device and network remaining connected. Reconnect ADB if necessary; the companion retries the reverse mapping.
- This is an early development build, not a security boundary against malicious software running as the same Windows or Android user.
