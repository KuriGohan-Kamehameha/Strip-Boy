# Checksums

Records the SHA-256 of the original Bethesda APK the patcher was
developed and tested against. If your `apk/original.apk` matches the
hash below, you're working from the exact same input bytes we are.

The patcher is structural — it looks up types and methods by name and
inserts IL relative to existing instructions, so other builds may still
work. But this is the known-good baseline.

## Fallout 4 Pip-Boy v1.2

| Field         | Value                                                            |
|---------------|------------------------------------------------------------------|
| package       | `com.bethsoft.falloutcompanionapp`                               |
| versionName   | `1.2`                                                            |
| versionCode   | `9`                                                              |
| size          | 39 585 934 bytes (≈37.8 MiB)                                      |
| sha256        | `974b8833af43def6640a4490a51f809cf1488244ca885df4c1f5632a145a91ce`|
| released      | 2016-03-22                                                       |
| platformBuild | Unity `5.x` / `6.0-2438415`                                      |

Verify:

```
shasum -a 256 apk/original.apk   # macOS / BSD
sha256sum    apk/original.apk     # Linux
Get-FileHash apk\original.apk -Algorithm SHA256   # Windows PowerShell
```
