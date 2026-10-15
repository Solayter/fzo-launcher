# FZO Launcher

A small Windows x64 launcher for the Friendzone Order Minecraft servers.
Written in C# / Windows Forms for .NET Framework 4.7.2 or newer.

Features: server selection, saved nickname, memory limit, configurable client
directory, copying existing installations, file verification, incremental updates,
game logs, recovery after interrupted updates, authenticated launcher self-updates,
and a standalone cyber cube icon for game windows (1.2.1+).

Official website: https://friendzone-order.duckdns.org/
Repository: https://github.com/Solayter/fzo-launcher

## Build and test

On Windows, with .NET Framework 4.7.2+ installed, run:

```powershell
.\test.ps1
.\build.ps1
```

The build uses the C# compiler included with .NET Framework and JDK 7/8 for
the original Java window-icon helper (Java 6 class format). Set `FZO_JAVA_HOME`
to the JDK directory; GitHub CI uses its preinstalled `JAVA_HOME_8_X64`. Players
do not need a separate JDK. The build does not need NuGet dependencies, Minecraft,
server credentials or signing keys.
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

## Game window icon (1.2.1+)

The launcher extracts its embedded, original Java helper to a content-addressed
JAR under `%LOCALAPPDATA%\FZO\launcher\bootstrap`. Its bytes are verified against
the embedded copy before every use. The helper invokes the original Minecraft
main with the same arguments and sets the standalone cyber cube on AWT windows
using Java's window-icon API. It handles recreated windows on the AWT event
thread. Game JARs, Java executables, worlds and settings are not patched. The
launcher and its shortcuts keep the owl artwork. Start the game through this
launcher to use the cube icon.

## Launcher self-updates (1.2.0+)

The launcher checks its own version at startup. The sidebar button also allows
manual checks. A new version is downloaded only after the user confirms; updates
are disabled during game installation and gameplay. Failures to check do not
prevent playing.

The signed catalog's optional `launcher` entry authenticates a four-part assembly
`version`, `sha256` and `size`. The EXE comes from the same content-addressed object
store as game files. A staged copy verifies the signed catalog and its own bytes
again, waits for the old launcher to exit, then atomically replaces the original
EXE and restarts it. The original is retained alongside it as `.backup-<id>`.
An immediate process-start failure restores that backup. A later startup crash
requires manual recovery from the backup. Protected installation folders may
require moving the launcher to a user-writable folder; no elevation is requested.
Settings and game files are not replaced. Verified staging files remain under
`%LOCALAPPDATA%\FZO\launcher\self-update-*` and can be deleted with the launcher
closed. The config format remains unchanged in this release.

Users of 1.1.x and earlier must manually download 1.2.0 once to gain this feature.
Publish a launcher release using `publish.py` with both `--launcher-exe` (the final
reviewed CI artifact, after Authenticode signing when available) and
`--launcher-version 1.2.0.0`, alongside the normal client arguments. A game-only
publication preserves the previous launcher entry only after the signer verifies
the previous catalog's signature. Always increment the assembly version for new
launcher binaries. Keep the previous download available for recovery.

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
