# ScreenKey Bridge

ScreenKey Bridge connects an Android phone to a Windows PC over an existing ADB connection. Capture the Windows virtual desktop to the phone's gallery and try the experimental PC-to-phone keyboard routing.

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

The companion periodically checks ADB and sets up `adb reverse tcp:38473 tcp:38473`. Right-click its tray icon to see connection status or select **Reconnect ADB** to retry immediately. After one successful connection using an IPv4 `adb tcpip` address, the companion saves that address locally and attempts `adb connect` to it when no device is connected. It then recreates the reverse mapping; it does not start wireless debugging on the phone. The phone and its ADB authorization must still be available, and an IP-address change, disabled wireless debugging, or a changed port may require a manual connection. The automatic reconnect applies to IPv4 `host:port` devices, not wireless-debugging mDNS names. If ADB is not on PATH, put platform-tools on PATH before starting the companion. If there are multiple devices, disconnect the others for now.

To start the companion when you sign in, right-click its tray icon and check **Start at Windows sign-in**. This is off by default and can be unchecked in the same menu. The per-user startup entry points to the executable's current location; if you move it, remove the old entry in Windows Startup settings and enable the option again from its new location.

The app captures the Windows virtual desktop, including all monitors, as one PNG; gaps between monitors are included. Capture while locked, protected content, and some display-sharing configurations may not work. No guarantee is made about visibility in third-party sharing or security tools.

The server binds only to Windows loopback and requires a random pairing key. The key is saved under your Windows user profile and in the Android app's private preferences; keep both devices and the key private. The tunnel is intended for a trusted ADB-connected device, not for exposing directly to a network. The Android app does not need broad storage access. Neither component sends screenshots to a cloud service.

## Experimental keyboard routing

1. In the Android app, save your pairing key by capturing a screen or pressing the keyboard toggle. Tap **Enable ScreenKey keyboard**, enable it in Android settings, and then use **Choose ScreenKey keyboard** to select it. Android displays a standard warning when enabling a keyboard; input methods can receive text you type through them.
2. Open a text field in the target phone app. Press **Ctrl+Alt+F11** on Windows or use the toggle shown on the ScreenKey keyboard. Alternatively, use the Android app's toggle and open a text field within 30 seconds.
3. Press **Ctrl+Alt+F12** to return to PC typing. Ctrl+Alt+F11 also toggles the mode. The function key used with Ctrl+Alt can be changed from the Windows tray menu; F12 is reserved for emergency return.
4. Use **Choose another keyboard** on the ScreenKey keyboard to return to your normal Android keyboard.

The phone app reports PC, waiting, or phone state. Redirected events are held briefly in memory and never written to a log. Routing returns to the PC if the input method loses its field, the Windows session locks, the connection lease expires, or an unsupported key is pressed. Waiting requests expire after 30 seconds. On a dropped connection, keystrokes already in transit can be lost; they are not replayed. Windows can silently remove a stalled low-level keyboard hook, so this prototype cannot guarantee detection of every hook failure. Check the destination before typing sensitive text.

The first version supports the US keyboard layout, basic letters, digits, punctuation, space, backspace, enter, and arrows. Other layouts, composed characters, shortcuts, and non-text fields are not supported reliably. Unsupported keys end routing and pass through to Windows. The ScreenKey input method is a bridge, not a full touch keyboard; use the keyboard picker to return to your usual one. Actual physical-keyboard handoff still needs broader testing on real devices.

## Current limitations

- Keyboard routing is experimental; monitor selection is not available yet.
- Pairing key rotation and selecting among multiple ADB devices are not yet exposed in the UI. To reset pairing, exit the companion, delete `%LOCALAPPDATA%\ScreenKeyBridge\pairing-key.txt`, and restart it; enter the new key on the phone.
- Wireless ADB connections depend on the device and network remaining connected. Reconnect ADB if necessary; the companion retries the reverse mapping.
- This is an early development build, not a security boundary against malicious software running as the same Windows or Android user.
