# HasladDNS

A Windows DNS profile manager with a focus on **IPv4 only**. The application is written in C# using Windows Forms and can be built with the .NET Framework C# compiler included with Windows.

## Features

- Apply saved IPv4 DNS profiles to network adapters.
- Add, edit, and remove custom profiles.
- Dark and light themes.
- Show current IPv4 DNS settings and refresh them periodically.
- Flush the Windows DNS resolver cache.
- Run an interactive `nslookup` diagnostic.
- Minimize to the system tray and switch profiles from the tray menu.
- Application icon embedded during build.

**IPv6 settings are not configured or modified.** Changing DNS settings requires administrator privileges.

## Build on Windows

1. Download or clone this repository.
2. Keep `HasladDNS_v1.cs`, `app.manifest`, `HasladDNS.ico`, and `build.bat` in the same directory.
3. Run `build.bat` on Windows 10 or Windows 11.
4. The script creates `HasladDNS_v1.exe` in the project directory.
5. Run the application as Administrator to apply DNS changes.

The build script uses the .NET Framework C# compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, falling back to the 32-bit framework path.

## User data

User profiles and theme preference are stored locally under:

```text
%AppData%\HB AI Smart DNS\
```

The profile file is `profiles.xml`; the theme preference is stored in `theme.txt`. These files are local application data and should not normally be committed to the repository.

## Notes

- DNS availability and compatibility depend on the selected provider and network.
- The current-DNS display is informational; it does not automatically overwrite DNS settings when a change is detected.
- Test the application on your own system before relying on it for network troubleshooting.

## Attribution

This project originated from/reuses an implementation based on the public HB AI Xbox DNS project. Preserve the original copyright and license notices in source files. Review the upstream license and ensure the required attribution is included before redistributing.
