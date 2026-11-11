# ScreenKey Bridge

ScreenKey Bridge connects an Android phone to a Windows PC over an existing ADB connection. Capture the Windows virtual desktop to the phone's gallery and try the experimental PC-to-phone keyboard routing.

## Requirements

- Windows 10 or 11, signed in to the interactive desktop; .NET 8 Desktop Runtime to run a framework-dependent build.
- Android 10 or later. ADB must already be authorized and connected, over USB or wireless.
- Android SDK platform-tools (`adb`) on the Windows PATH, or the copy bundled with the Windows installer. Exactly one authorized ADB device should be connected.
- For development: .NET 8 SDK, JDK 17, Android SDK platform 35 and Gradle 8.9.

## Build and run

1. Build the companion: `dotnet build windows/ScreenKeyBridge/ScreenKeyBridge.csproj -c Release`.
2. Build the Android debug app: from `android`, run `gradle assembleDebug`, then install with `adb install -r app/build/outputs/apk/debug/app-debug.apk`.
3. Start `windows/ScreenKeyBridge/bin/Release/net8.0-windows/ScreenKeyBridge.exe` in your signed-in Windows session. It runs in the notification area. Right-click its icon and select **Pair connected phone**. This requires one authorized ADB device with the Android app installed. Alternatively, select **Show pairing key and status** and enter the key manually.
4. In the phone app, choose **All displays** or a specific monitor, and tap **Capture PC screen**. Images appear in `Pictures/ScreenKey Bridge` in the phone's gallery and are also copied to the phone clipboard. Each capture replaces the previous clipboard contents. Use **Refresh displays** after connecting or disconnecting monitors. If a selected monitor is unavailable, capture reports an error rather than silently switching to another monitor.

## Capture without opening the app

- On Windows, press **Ctrl+Alt+F10** to capture immediately and send the image to your phone. Change the function key under **Screenshot shortcut (Ctrl+Alt)** in the tray menu. F12 is reserved for returning keyboard input to Windows, and the screenshot and keyboard-routing shortcuts must differ. The tray menu's **Last screenshot** reports whether the phone confirmed the save.
- On Android, add the **Capture PC screen** Quick Settings tile using the button in the app (Android 13+) or the Quick Settings edit screen. Tap that tile from another app to capture and save without opening ScreenKey Bridge.
- Both methods save the image to the gallery and copy it to the phone clipboard, replacing its previous contents. They use the display selected in the phone app. The app syncs the selection to the PC when connected; if the PC was disconnected, open the app after reconnecting to sync it. If the selected display is unavailable, the capture fails instead of choosing another display.
- Android runs a short foreground service while saving. It may show a brief phone notification; enable capture notifications in the app for saved/failed notifications. The tile also shows a brief result. The companion and ADB connection must be active, and the phone must have been unlocked since boot. Force-stopping the Android app can prevent background capture until it is opened again.

The Windows shortcut captures the selected screen into memory before asking the phone to fetch and save it; no desktop screenshot file is created. One shortcut capture can be in flight at a time, and an unconfirmed image is discarded after 30 seconds. PC capture status is available in the tray rather than a desktop popup.

The companion periodically checks ADB and sets up `adb reverse tcp:38473 tcp:38473`. Right-click its tray icon to see connection status or select **Reconnect ADB** to retry immediately. After one successful connection using an IPv4 `adb tcpip` address, the companion saves that address locally and attempts `adb connect` to it when no device is connected. It then recreates the reverse mapping; it does not start wireless debugging on the phone. The phone and its ADB authorization must still be available, and an IP-address change, disabled wireless debugging, or a changed port may require a manual connection. The automatic reconnect applies to IPv4 `host:port` devices, not wireless-debugging mDNS names. If ADB is not on PATH, put platform-tools on PATH before starting the companion. If there are multiple devices, disconnect the others for now.

To start the companion when you sign in, right-click its tray icon and check **Start at Windows sign-in**. This is off by default and can be unchecked in the same menu. The per-user startup entry points to the executable's current location; if you move it, remove the old entry in Windows Startup settings and enable the option again from its new location.

When **All displays** is selected, the app captures the Windows virtual desktop as one PNG; gaps between monitors are included. Capture while locked, protected content, and some display-sharing configurations may not work. No guarantee is made about visibility in third-party sharing or security tools.

The server binds only to Windows loopback and requires a random pairing key. The key is saved under your Windows user profile and in the Android app's private preferences; keep both devices and the key private. Tray pairing sends the key through the already-authorized ADB shell to an Android receiver restricted to the shell/system; like ADB itself, it assumes a trusted PC and phone. The tunnel is intended for a trusted ADB-connected device, not for exposing directly to a network. The Android app does not need broad storage access. Neither component sends screenshots to a cloud service.

## Experimental keyboard routing

1. In the Android app, save your pairing key by capturing a screen or pressing the keyboard toggle. Tap **Enable ScreenKey keyboard**, enable it in Android settings, and then use **Choose ScreenKey keyboard** to select it. Android displays a standard warning when enabling a keyboard; input methods can receive text you type through them.
2. Open a text field in the target phone app. Press **Ctrl+Alt+F11** on Windows or use the toggle shown on the ScreenKey keyboard. Alternatively, use the Android app's toggle and open a text field within 30 seconds.
3. Press **Ctrl+Alt+F12** to return to PC typing. Ctrl+Alt+F11 also toggles the mode. The function key used with Ctrl+Alt can be changed from the Windows tray menu; F12 is reserved for emergency return.
4. Use **Choose another keyboard** on the ScreenKey keyboard to return to your normal Android keyboard.

The phone app reports PC, waiting, or phone state. Redirected events are held briefly in memory and never written to a log. Routing returns to the PC if the input method loses its field, the Windows session locks, the connection lease expires, the queue fills, or a toggle or emergency shortcut is used. Unsupported keys and shortcuts stay in phone mode and show a notice on the ScreenKey keyboard. Waiting requests expire after 30 seconds. On a dropped connection, keystrokes already in transit can be lost; they are not replayed. Windows can silently remove a stalled low-level keyboard hook, so this prototype cannot guarantee detection of every hook failure. Check the destination before typing sensitive text.

The bridge supports US-layout letters, digits and punctuation, editing and navigation keys, Ctrl+A/C/X/V, Ctrl+Z/Y, Ctrl+Backspace, Ctrl+Delete, and modified arrow navigation. It also includes a basic on-screen QWERTY keyboard with Shift, numbers, symbols and a Paste key. Ctrl+V and the touch Paste key use the **phone's** clipboard; Ctrl+C and Ctrl+X also use the phone clipboard. For a captured image, the keyboard sends rich image content when the target explicitly supports it, or requests a normal paste otherwise. The receiving app must support image paste: plain text fields and URL bars generally cannot accept an image, and some apps don't implement image paste even in rich editors. Undo, navigation, and clipboard actions depend on support in the target Android app. Android may clear the clipboard after a period of time. Other keyboard layouts, predictive text, swipe typing, composed characters, and non-text fields are not supported reliably.

## Building installable packages

`scripts/build-windows-installer.ps1` builds a per-user x64 Windows installer in `dist`. It needs the .NET 8 SDK, Inno Setup's `ISCC.exe`, and Android platform-tools (including `NOTICE.txt`). Pass `-Dotnet`, `-Iscc`, and `-PlatformTools` if they are not on PATH. The installer bundles ADB and uses an existing PATH installation when available. Exit any running companion from its tray menu before running the installer. If sign-in startup was enabled for a portable copy, disable it from that copy before moving to the installed one, then enable it from the installed copy if desired. The installer is not code-signed.

`scripts/build-android-apks.ps1` builds two APKs in `dist`; set `JAVA_HOME` and `ANDROID_HOME`, and pass `-Gradle` if Gradle 8.9 is not on PATH. The debug APK is signed with the current machine's Android debug key and can update the existing debug installation made with that same key. The release APK is signed with a separate private release key, so Android cannot install it over the debug-signed app. Do not uninstall an existing app just to change signing keys without first considering its local settings; gallery images are separate from those settings.

The Android packaging script creates the release key and an encrypted password file once under `%LOCALAPPDATA%\ScreenKeyBridge\signing`; it never commits either to the repository. Keep a secure backup of the keystore **and its password** before distributing the release APK, because future updates must use the same key. The `.dpapi` password file is protected by the current Windows account and is not a portable backup by itself. While signed in to that Windows account, you can retrieve the password locally with the following commands and store it in a password manager. Never share the password or keystore publicly:

```powershell
$secret = Get-Content "$env:LOCALAPPDATA\ScreenKeyBridge\signing\release-password.dpapi" -Raw | ConvertTo-SecureString
[System.Net.NetworkCredential]::new('', $secret).Password
```

## Current limitations

- Keyboard routing is experimental and shortcut behavior varies across Android apps.
- Pairing key rotation and selecting among multiple ADB devices are not yet exposed in the UI. To reset pairing, exit the companion, delete `%LOCALAPPDATA%\ScreenKeyBridge\pairing-key.txt`, and restart it; enter the new key on the phone.
- Wireless ADB connections depend on the device and network remaining connected. Reconnect ADB if necessary; the companion retries the reverse mapping.
- This is an early development build, not a security boundary against malicious software running as the same Windows or Android user.
