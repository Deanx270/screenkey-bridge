# ScreenKey Bridge

ScreenKey Bridge connects an Android phone to a Windows PC over an existing ADB connection. Capture the Windows virtual desktop to the phone's gallery and try the experimental PC-to-phone keyboard routing.

## License

ScreenKey Bridge is available under the [MIT License](LICENSE). Bundled third-party components are subject to their own licenses and notices.

## Requirements

- Windows 10 or 11, signed in to the interactive desktop; .NET 8 Desktop Runtime to run a framework-dependent build.
- Android 10 or later. ADB must already be authorized and connected, over USB or wireless.
- Android SDK platform-tools (`adb`) on the Windows PATH, or the copy bundled with the Windows installer. Choose an authorized device if more than one is connected.
- For development: .NET 8 SDK, JDK 17, Android SDK platform 35 and Gradle 8.9.

## Build and run

1. Build the companion: `dotnet build windows/ScreenKeyBridge/ScreenKeyBridge.csproj -c Release`.
2. Build the Android debug app: from `android`, run `gradle assembleDebug`, then install with `adb install -r app/build/outputs/apk/debug/app-debug.apk`.
3. Start `windows/ScreenKeyBridge/bin/Release/net8.0-windows/ScreenKeyBridge.exe` in your signed-in Windows session. It runs in the notification area. Select the normal or preview Android installation in the tray, then choose **Connect and pair a phone**. Select an authorized device and choose **Connect and pair selected phone**. The companion transfers its generated key automatically. Pairing the same Android installation with a different PC replaces its previous pairing.
4. In the phone app, choose **All displays** or a specific monitor, and tap **Capture PC screen**. Images appear in `Pictures/ScreenKey Bridge` in the phone's gallery and are also copied to the phone clipboard. Each capture replaces the previous clipboard contents. Use **Refresh displays** after connecting or disconnecting monitors. If a selected monitor is unavailable, capture reports an error rather than silently switching to another monitor.

## Capture without opening the app

- On Windows, press **Ctrl+Alt+F10** to capture immediately and send the image to your phone. Change the function key under **Screenshot shortcut (Ctrl+Alt)** in the tray menu. F12 is reserved for returning keyboard input to Windows, and the screenshot and keyboard-routing shortcuts must differ. The tray menu's **Last screenshot** reports whether the phone confirmed the save.
- On Android, add the **Capture PC screen** Quick Settings tile using the button in the app (Android 13+) or the Quick Settings edit screen. Tap that tile from another app to capture and save without opening ScreenKey Bridge.
- Both methods save the image to the gallery and copy it to the phone clipboard, replacing its previous contents. They use the display selected in the phone app. The app syncs the selection to the PC when connected; if the PC was disconnected, open the app after reconnecting to sync it. If the selected display is unavailable, the capture fails instead of choosing another display.
- Android runs a short foreground service while saving. It may show a brief phone notification; enable capture notifications in the app for saved/failed notifications. The tile also shows a brief result. The companion and ADB connection must be active, and the phone must have been unlocked since boot. Force-stopping the Android app can prevent background capture until it is opened again.

The Windows shortcut captures the selected screen into memory before asking the phone to fetch and save it; no desktop screenshot file is created. One shortcut capture can be in flight at a time, and an unconfirmed image is discarded after 30 seconds. PC capture status is available in the tray rather than a desktop popup.

## Save the current Edge page to the phone

In the packaged Windows companion, select **Set up Edge page saving** from the tray. In Edge, open `edge://extensions`, turn on Developer mode, select **Load unpacked**, and select the `edge-extension` folder opened by the companion. If the extension was already installed, select **Reload** instead. Edge asks for permission to read website data; the extension accesses the current page only when the paired phone requests a save. Keep the extension enabled and the companion running.

Bring the desired Edge window to the foreground on the PC. In the Android app choose **HTML** (the default) or **PDF** and tap **Save current Edge page to phone**. HTML is stored in `Downloads/ScreenKey Bridge`; for PDF, Android's print screen opens on the phone, where you choose **Save as PDF** and its destination. The PC does not create a browser download. Page content is held only in memory on the PC and sent over the authenticated phone connection. The Edge extension connects to the loopback-only companion using a separate generated credential in the local user profile and extension folder; that credential is not sent to the phone.

Alternatively, press **Ctrl+Alt+F8** on the PC to save HTML in the phone's Downloads, or **Ctrl+Alt+F9** to open the phone's PDF print screen. For PDF, unlock the phone, choose **Save as PDF** and complete the save; opening the print screen alone does not save a file. The page and keyboard shortcuts must use different function keys; change them from the Windows tray menu if necessary. If an existing preference conflicts with a default, the companion selects an unused key and shows the actual key in **Show status**. The tray's **Last page save** reports progress or errors.

HTML saves the current DOM with scripts and form submissions disabled. Already-loaded images are embedded when browser security permits; other images, styles and fonts may remain linked, need internet access, or require a phone-side login. PDF is rendered from this snapshot on the phone and can differ from the PC page. Interactive applications, embedded frames, protected pages, and browser-internal pages may not save correctly. Do not assume a saved HTML page is fully offline or contains all content.

The companion checks ADB every five seconds and sets up `adb reverse tcp:38473 tcp:38473` for the selected connection. Right-click its tray icon to see the last checked status or select **Reconnect ADB** to retry the saved Wi-Fi address. Wireless ADB auto-reconnect is off by default, so a command-line `adb disconnect` is not undone by the companion. You may enable **Automatically reconnect wireless ADB** in the tray if you prefer automatic retries; that also means a command-line disconnect can be reversed. **Connect and pair a phone** can enable TCP port 5555 on an authorized USB-connected phone and connect to an IPv4 address entered by the user. The phone must have USB debugging enabled and the user must approve the device's authorization prompt. The phone and its ADB authorization must still be available, and an IP-address change, disabled wireless debugging, or a changed port may require reconnecting. The guided Wi-Fi path uses TCP mode and does not pair Android's separate wireless-debugging pairing-code flow. If multiple devices are connected, choose one in the setup window. The companion remembers that selection and does not silently switch devices if it becomes unavailable.

To start the companion when you sign in, right-click its tray icon and check **Start at Windows sign-in**. This is off by default and can be unchecked in the same menu. The per-user startup entry points to the executable's current location; if you move it, remove the old entry in Windows Startup settings and enable the option again from its new location.

When **All displays** is selected, the app captures the Windows virtual desktop as one PNG; gaps between monitors are included. Capture while locked, protected content, and some display-sharing configurations may not work. No guarantee is made about visibility in third-party sharing or security tools.

The server binds only to Windows loopback and requires a random pairing key. The key is saved under your Windows user profile and in the Android app's private preferences; keep both devices and the key private. Tray pairing sends the key through the already-authorized ADB shell to an Android receiver restricted to the shell/system; like ADB itself, it assumes a trusted PC and phone. The tunnel is intended for a trusted ADB-connected device, not for exposing directly to a network. The Android app does not need broad storage access. Neither component sends screenshots to a cloud service.

## Experimental keyboard routing

1. After pairing from Windows, open the Android app. Tap **Enable ScreenKey keyboard**, enable it in Android settings, and then use **Choose ScreenKey keyboard** to select it. Android displays a standard warning when enabling a keyboard; input methods can receive text you type through them.
2. Open a text field in the target phone app. Press **Ctrl+Alt+F11** on Windows or use the toggle shown on the ScreenKey keyboard. Alternatively, use the Android app's toggle and open a text field within 30 seconds.
3. Press **Ctrl+Alt+F12** to return to PC typing. Ctrl+Alt+F11 also toggles the mode. The function key used with Ctrl+Alt can be changed from the Windows tray menu; F12 is reserved for emergency return.
4. Use **Choose another keyboard** on the ScreenKey keyboard to return to your normal Android keyboard.

The phone app reports PC, waiting, or phone state. Redirected events are held briefly in memory and never written to a log. Routing returns to the PC if the input method loses its field, the Windows session locks, the connection lease expires, the queue fills, or a toggle or emergency shortcut is used. Unsupported keys and shortcuts stay in phone mode and show a notice on the ScreenKey keyboard. Waiting requests expire after 30 seconds. On a dropped connection, keystrokes already in transit can be lost; they are not replayed. Windows can silently remove a stalled low-level keyboard hook, so this prototype cannot guarantee detection of every hook failure. Check the destination before typing sensitive text.

The bridge supports US-layout letters, digits and punctuation, editing and navigation keys, Ctrl+A/C/X/V, Ctrl+Z/Y, Ctrl+Backspace, Ctrl+Delete, and modified arrow navigation. It also includes a basic on-screen QWERTY keyboard with Shift, numbers, symbols and a Paste key. Ctrl+V and the touch Paste key use the **phone's** clipboard; Ctrl+C and Ctrl+X also use the phone clipboard. For a captured image, the keyboard sends rich image content when the target explicitly supports it, or requests a normal paste otherwise. The receiving app must support image paste: plain text fields and URL bars generally cannot accept an image, and some apps don't implement image paste even in rich editors. Undo, navigation, and clipboard actions depend on support in the target Android app. Android may clear the clipboard after a period of time. Other keyboard layouts, predictive text, swipe typing, composed characters, and non-text fields are not supported reliably.

## Building installable packages

`scripts/build-windows-installer.ps1` builds a per-user x64 Windows installer in `dist`. It needs the .NET 8 SDK, Inno Setup's `ISCC.exe`, and Android platform-tools (including `NOTICE.txt`). Pass `-Dotnet`, `-Iscc`, and `-PlatformTools` if they are not on PATH. The installer bundles ADB and uses an existing PATH installation when available. Exit any running companion from its tray menu before running the installer. If sign-in startup was enabled for a portable copy, disable it from that copy before moving to the installed one, then enable it from the installed copy if desired. The installer is not code-signed.

`scripts/build-android-apks.ps1` builds three APKs in `dist`; set `JAVA_HOME` and `ANDROID_HOME`, and pass `-Gradle` if Gradle 8.9 is not on PATH. The debug APK can update an installation signed with this machine's debug key. The release APK uses a separate private release key, so Android cannot install it over the debug-signed app. The **preview** APK uses that same release key but a different package ID, so it can be installed alongside the existing debug app without deleting its settings. In the Windows tray select **Android app installation → Side-by-side preview**, then pair again; select **ScreenKey Bridge Preview Keyboard** in Android when testing its keyboard. Return the tray selection and Android keyboard to the normal app when finished. Only one PC should hold the ADB connection to a phone during testing, since both PCs would otherwise compete for the same reverse port.

To move a wireless phone between PCs, select **Disconnect wireless phone** in the first PC's tray menu, then exit its companion before connecting from the other PC. The menu pauses connection checks for that running companion; use **Reconnect ADB** to resume them and try the saved address. This disconnects the selected wireless phone, or the only wireless phone if no selection was made. If automatic reconnect is enabled, reopening the first companion may reconnect it, so exit and disconnect the other PC before switching back.

The side-by-side preview is for testing: because its package ID differs, it cannot be upgraded in place to the normal release APK. The normal release and debug APKs still cannot update one another because their signatures differ. Do not uninstall an existing app just to change signing keys without first considering its local settings; gallery images are separate from those settings.

The Android packaging script creates the release key and an encrypted password file once under `%LOCALAPPDATA%\ScreenKeyBridge\signing`; it never commits either to the repository. Keep a secure backup of the keystore **and its password** before distributing the release APK, because future updates must use the same key. The `.dpapi` password file is protected by the current Windows account and is not a portable backup by itself. While signed in to that Windows account, you can retrieve the password locally with the following commands and store it in a password manager. Never share the password or keystore publicly:

```powershell
$secret = Get-Content "$env:LOCALAPPDATA\ScreenKeyBridge\signing\release-password.dpapi" -Raw | ConvertTo-SecureString
[System.Net.NetworkCredential]::new('', $secret).Password
```

## Current limitations

- Keyboard routing is experimental and shortcut behavior varies across Android apps.
- Pairing key rotation is not yet exposed in the UI. To reset pairing, exit the companion, delete `%LOCALAPPDATA%\ScreenKeyBridge\pairing-key.txt`, restart it, and pair the selected phone again.
- Wireless ADB connections depend on the device and network remaining connected. Use **Reconnect ADB** if necessary, or opt in to automatic wireless retries; the companion refreshes the reverse mapping while connected.
- This is an early development build, not a security boundary against malicious software running as the same Windows or Android user.
