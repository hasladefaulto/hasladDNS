# HasladDNS 1.1 — work in progress

## Implemented in the patch branch

- Added `settings.json` for app settings while preserving `profiles.xml` and `theme.txt`.
- Track the three most recently applied DNS profiles.
- Detect changes to the visible IPv4 DNS configuration and notify through the system tray when a change appears external.
- Check DNS resolution periodically; notify after three consecutive failures and when resolution recovers.
- Notify when applying a DNS profile fails.
- Added tray commands to view recent profiles, toggle Windows startup, and toggle DNS-change notifications.
- Warn once when IPv6 DNS servers are configured, because this app only changes IPv4 DNS.
- Added the .NET Framework assembly reference required for JSON serialization.

## Verification still required

- Compile with the Windows .NET Framework C# compiler.
- Exercise profile apply/disable, external DNS change detection, startup toggle, settings persistence, IPv6 warning, and tray notifications on Windows.
- Verify DNS health notifications under captive portals and transient network failures.
