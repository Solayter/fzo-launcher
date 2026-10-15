# FZO Launcher

A small Windows x64 launcher for the Friendzone Order Minecraft servers.
Written in C# / Windows Forms for .NET Framework 4.7.2 or newer.

Features: server selection, saved nickname, memory limit, configurable client
directory, copying existing installations, file verification, incremental updates,
game logs, and recovery after interrupted updates.

Official website: https://friendzone-order.duckdns.org/
Repository: https://github.com/Solayter/fzo-launcher

## Build and test

On Windows, with .NET Framework 4.7.2+ installed, run:

```powershell
.\test.ps1
.\build.ps1
```

The build uses the C# compiler included with .NET Framework. It does not need
NuGet dependencies, a Minecraft installation, server credentials or signing keys.
Output: `dist/FZO-Launcher.exe`. Artwork and the **public** update verification
key are embedded in the executable. No private keys belong in this repository.

## Update security

Starting with 1.1.0, the launcher requires `launcher/catalog.signed.json`:

* RSA-3072 / SHA-256 PKCS#1 v1.5 signature, verified against the embedded key.
* The signed catalog authenticates the SHA-256 of each client's manifest.
* Each manifest authenticates the size and SHA-256 of the downloaded files.
* Catalogs have increasing sequence numbers and a maximum 180-day validity.
* Previously seen catalogs are cached to reject rollback to older sequences.
* There is no fallback to an unsigned catalog.

Publishers must refresh the signed catalog before it expires. On a first
installation, an older **still valid** signed catalog may be replayed until its
expiry; the local sequence cache protects clients after they have seen a newer
catalog. This is not a complete implementation of TUF.

This update signature is separate from Windows Authenticode. The current EXE
is not Authenticode-signed. Upgrading from 1.0.x requires downloading the new EXE
manually: old launchers do not gain signature verification automatically.

See [SECURITY.md](SECURITY.md), [PRIVACY.md](PRIVACY.md), and
[CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Data and uninstall

Settings are stored in `%LOCALAPPDATA%\FZO\launcher`; clients default to
`%LOCALAPPDATA%\FZO\clients\beta` and `industrial`. A different client root
can be selected in the menu. The launcher does not use the normal `.minecraft`.

To uninstall, close Minecraft and the launcher, delete the launcher EXE and its
optional config, and delete `%LOCALAPPDATA%\FZO\launcher`. Delete the chosen
client directories only if you no longer need their worlds, screenshots and
settings; back them up first. No service, startup entry or scheduled task is
installed.

Minecraft, Java, mods and complete game distributions are **not included** in
this source repository and are not relicensed by this project. External client
distributions have their own terms. The publisher must ensure distribution is
authorized; this repository's license does not grant those rights.

## License

GPL-3.0-only for the project's original code and build scripts. See [LICENSE](LICENSE).
See [NOTICE.md](NOTICE.md) for artwork and third-party notices. The license does
not grant permission to impersonate Friendzone Order or imply official endorsement.
