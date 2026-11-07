# DoorMan

Password protection for Windows 10/11 folders, built into File Explorer. DoorMan encrypts a folder's contents and asks
for the password *before* anything inside can be seen or opened, whether from Explorer or from any other app.

Available in English, Spanish, French, Chinese (Simplified) and Hindi. It follows the Windows display language and
falls back to English.

## Download and install

Get the file for your PC from the [latest release](https://github.com/cristhobal/drmn/releases/latest):

| Your Windows | File |
|---|---|
| Windows 11 or 64-bit Windows 10 (most PCs) | `DoorMan-x64.exe` |
| Windows 11 or Windows 10 on ARM (Snapdragon, etc.) | `DoorMan-arm64.exe` |
| 32-bit Windows 10 | `DoorMan-x86.exe` |

Not sure? Open **Settings → System → About** and look at **System type**. If you pick the wrong file, DoorMan tells
you which one to download.

1. Double-click the downloaded file and choose **Install**. Nothing else is needed (.NET is included).
2. On Windows 11, Windows asks once for permission (UAC) to add DoorMan to the context menu. If you decline, the
   options stay under **Show more options**.
3. Done. DoorMan installs for your user only, in `%LOCALAPPDATA%\Programs\DoorMan`, and starts silently with Windows.
   Uninstall it from **Settings → Apps**.

The executable is not code-signed, so Windows SmartScreen may warn that it is an unrecognized app: choose
**More info → Run anyway**.

## Usage

1. Right-click a folder → **Protect with password**. Enter and confirm the password.
2. Open the locked folder as usual (double-click). DoorMan asks for the password first; once it is correct, the folder
   is decrypted and Explorer goes straight into it. A wrong password lets you try again.
3. Opening a file of a locked folder from anywhere else (recent files, an app's jump list, the command line…) also asks
   for the password; when it is correct, the folder is unlocked and the app gets the file.
4. When you are done, right-click the folder → **Lock**. It is locked again with the same password.

The context menu adapts to each folder: unprotected folders show **Protect with password**, locked folders show
**Unlock** and unlocked ones show **Lock**. Locked folders show a padlock icon (empty or with content); unlocked
folders keep the normal Windows folder icon. Errors are reported with a native Windows notification. On Windows 11,
**Unlock** is under **Show more options**: Windows shows no packaged commands on locked folders (double-clicking them
asks for the password anyway).

## How it works

- **Encryption.** All contents (files, subfolders, attributes and timestamps) are packed into a single hidden file,
  `Unlock folder.doorman`, encrypted with AES-256-GCM in 1 MiB chunks. The key is derived from the password with
  PBKDF2-SHA256 (600,000 iterations).
- **Placeholders.** A locked folder is registered as a sync root with the Windows Cloud Files API (the mechanism behind
  OneDrive Files On-Demand). Every file is left as a placeholder with its name, size and dates but **no content**: the
  data only exists in the encrypted file. When any app tries to read a placeholder, Windows asks DoorMan for the data,
  DoorMan asks for the password, unlocks the folder and hands the file over. If the volume does not support
  placeholders (not NTFS, network drives…), the originals are deleted instead and the folder only holds the encrypted
  file.
- **Background agent.** `DoorMan.exe --agent` (started at sign-in) serves those requests and runs the context menu
  commands, which reach it through a named pipe restricted to the current user. Without it, apps opening a locked file
  get "the cloud file provider is not running" and the content stays encrypted.
- **Data safety.** The encrypted file is fully written and decrypted again to verify it (SHA-256 of every file)
  *before* any original is removed. Unlocking decrypts everything into a hidden staging folder first; if that fails,
  the folder stays locked and untouched.
- **Explorer extension.** `DoorManShell.dll` is a native C++ shell extension (`IShellFolder2`). The `desktop.ini` of a
  locked folder points to it, so when Explorer tries to open the folder the extension cancels the navigation and asks
  for the password instead. Once unlocked, it forwards everything to the regular file system folder. Quick Access
  entries are refreshed when locking so they also go through the extension.
- **Relocking.** While a folder is unlocked, its key is stored protected with DPAPI (readable only by your Windows user)
  in `%LOCALAPPDATA%\DoorMan`. It is deleted when the folder is locked again.
- **Context menu.** Menu entries are filtered per folder with `AppliesTo` on `System.ParsingPath` (the real path, not the
  localized display path). DoorMan never changes the default double-click action of folders.
- **Windows 11 context menu.** The modern menu only shows commands of apps with package identity, so DoorMan installs a
  sparse package (`DoorMan.msix`, pointing to the install folder) that declares an `IExplorerCommand` from
  `DoorManShell.dll`. It shows **Protect with password** or **Lock** depending on the folder (unlocked folders are
  listed in `%LOCALAPPDATA%\DoorMan\unlocked.txt`), and replaces the registry entries. Windows only installs it if
  signed with a machine-trusted certificate: each build creates a new self-signed one, signs the package and destroys
  the private key right away; the installer trusts the certificate (UAC) and removes those of previous builds.
- **Single-file installer.** Each `DoorMan-<arch>.exe` is a self-contained .NET app that carries the Explorer extension
  for its architecture, the icons and the Windows 11 package as embedded resources, and extracts them when installing.

## Limitations

- If you forget the password, the contents **cannot be recovered**.
- While a folder is locked, file **names, sizes and dates remain visible** (the content does not).
- Files that are open in another app when locking keep their content until they are closed; DoorMan empties them then.
- Originals are removed normally; on hard disk drives, forensic tools might recover remnants. For maximum security, also
  use BitLocker.
- Drive roots, system folders, the user profile and whole personal folders (Desktop, Documents…) cannot be protected,
  nor folders with symbolic links or OneDrive files that are not downloaded.
- Custom NTFS permissions and alternate data streams are not preserved.
- If a folder is moved or renamed while unlocked, locking it again asks for a new password.
- A new version of the Explorer extension is loaded after Explorer restarts (or after signing out).

## Development

Requires the .NET 10 SDK, the Visual Studio C++ build tools (x64, x86 and ARM64) and the Windows SDK (`makeappx`,
`signtool`).

```powershell
dotnet test tests\DoorMan.Tests   # unit tests
```

The build and packaging scripts (icons, native extension, signed MSIX) are kept out of this repository.

User-facing texts live in `src/DoorMan/Strings.cs` (and the three Windows 11 menu titles in
`native/DoorManShell/DoorManShell.cpp`); to add a language, add a value to `Language` and a translation to each entry.

Command-line arguments: `--protect`, `--open`, `--lock` + folder path; `--install`, `--uninstall`, `--register`,
`--unregister`, `--startup`, `--agent`.
