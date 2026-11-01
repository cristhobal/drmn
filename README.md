# DoorMan

Password protection for Windows 10/11 folders, built into File Explorer. DoorMan encrypts a folder's contents and asks
for the password *before* Explorer shows them.

> The app's user interface is in Spanish.

## Usage

1. Install: run `publish\CarpetaProtegida.exe --instalar`. It installs per user (no administrator rights needed) to
   `%LOCALAPPDATA%\Programs\CarpetaProtegida` and starts silently in the background with Windows.
2. Right-click a folder → **Proteger con contraseña** (Protect with password). On Windows 11 the option is under
   **Show more options**. Enter and confirm the password.
3. Open the locked folder as usual (double-click). DoorMan asks for the password first; once it is correct, the folder
   is decrypted and Explorer goes straight into it. A wrong password lets you try again.
4. When you are done, right-click the folder → **Bloquear** (Lock). It is locked again with the same password.

The context menu adapts to each folder: unprotected folders show **Proteger con contraseña**, locked folders show
**Desbloquear** (Unlock) and unlocked ones show **Bloquear** (Lock). Folder icons show whether a folder is locked or
unlocked, and empty or not. Errors are reported with a native Windows notification.

## How it works

- **Encryption.** All contents (files, subfolders, attributes and timestamps) are packed into a single hidden file,
  `Desbloquear carpeta.cpbloq`, encrypted with AES-256-GCM in 1 MiB chunks. The key is derived from the password with
  PBKDF2-SHA256 (600,000 iterations). While locked, the folder holds only that file and a `desktop.ini`, so no other
  application or command-line tool can read the contents or even the file names.
- **Data safety.** The encrypted file is fully written and decrypted again to verify it (SHA-256 of every file)
  *before* any original is deleted. If something fails earlier, the folder is left untouched; if unlocking fails midway,
  it is rolled back and the folder stays locked.
- **Explorer extension.** `CarpetaProtegidaShell.dll` is a native C++ shell extension (`IShellFolder2`). The
  `desktop.ini` of a locked folder points to it, so when Explorer tries to open the folder the extension cancels the
  navigation and asks for the password instead. Once unlocked, it forwards everything to the regular file system folder.
- **Relocking.** While a folder is unlocked, its key is stored protected with DPAPI (readable only by your Windows user)
  in `%LOCALAPPDATA%\CarpetaProtegida`. It is deleted when the folder is locked again.
- **Context menu.** Menu entries are filtered per folder with `AppliesTo` on `System.ParsingPath` (the real path, not the
  localized display path). DoorMan never changes the default double-click action of folders.

## Limitations

- If you forget the password, the contents **cannot be recovered**.
- Originals are deleted normally; on hard disk drives, forensic tools might recover remnants. For maximum security, also
  use BitLocker.
- Drive roots, system folders, the user profile and whole personal folders (Desktop, Documents…) cannot be protected,
  nor folders with symbolic links or OneDrive files that are not downloaded.
- Custom NTFS permissions and alternate data streams are not preserved.
- If a folder is moved or renamed while unlocked, locking it again asks for a new password.
- A new version of the Explorer extension is loaded after Explorer restarts (or after signing out).

## Development

Requires the .NET 10 SDK and the Visual Studio C++ build tools.

```powershell
dotnet test tests\CarpetaProtegida.Tests   # unit tests
.\publicar.ps1                             # icons, native extension and publish\CarpetaProtegida.exe
```

Command-line arguments: `--proteger`, `--abrir`, `--rebloquear` + folder path; `--instalar`, `--desinstalar`,
`--registrar`, `--desregistrar`, `--inicio`.
