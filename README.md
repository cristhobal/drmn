# DoorMan

Password protection for Windows 10/11 folders, built into File Explorer. DoorMan encrypts a folder's contents and asks
for the password *before* anything inside can be seen or opened, whether from Explorer or from any other app.

> The app's user interface is in Spanish.

## Usage

1. Install: run `publish\DoorMan.exe --instalar`. It installs per user (no administrator rights needed) to
   `%LOCALAPPDATA%\Programs\DoorMan` and runs silently in the background from sign-in.
2. Right-click a folder → **Proteger con contraseña** (Protect with password). On Windows 11 the option is under
   **Show more options**. Enter and confirm the password.
3. Open the locked folder as usual (double-click). DoorMan asks for the password first; once it is correct, the folder
   is decrypted and Explorer goes straight into it. A wrong password lets you try again.
4. Opening a file of a locked folder from anywhere else (recent files, an app's jump list, the command line…) also asks
   for the password; when it is correct, the folder is unlocked and the app gets the file.
5. When you are done, right-click the folder → **Bloquear** (Lock). It is locked again with the same password.

The context menu adapts to each folder: unprotected folders show **Proteger con contraseña**, locked folders show
**Desbloquear** (Unlock) and unlocked ones show **Bloquear** (Lock). Folder icons show whether a folder is locked or
unlocked, and empty or not. Errors are reported with a native Windows notification.

## How it works

- **Encryption.** All contents (files, subfolders, attributes and timestamps) are packed into a single hidden file,
  `Desbloquear carpeta.doorman`, encrypted with AES-256-GCM in 1 MiB chunks. The key is derived from the password with
  PBKDF2-SHA256 (600,000 iterations).
- **Placeholders.** A locked folder is registered as a sync root with the Windows Cloud Files API (the mechanism behind
  OneDrive Files On-Demand). Every file is left as a placeholder with its name, size and dates but **no content**: the
  data only exists in the encrypted file. When any app tries to read a placeholder, Windows asks DoorMan for the data,
  DoorMan asks for the password, unlocks the folder and hands the file over. If the volume does not support
  placeholders (not NTFS, network drives…), the originals are deleted instead and the folder only holds the encrypted
  file.
- **Background agent.** `DoorMan.exe --agente` (started at sign-in) serves those requests and runs the context-menu
  commands, which reach it through a named pipe restricted to the current user. Without it, apps opening a locked file
  get "the cloud file provider is not running" and the content stays encrypted.
- **Data safety.** The encrypted file is fully written and decrypted again to verify it (SHA-256 of every file)
  *before* any original is removed. Unlocking decrypts everything into a hidden staging folder first; if that fails,
  the folder stays locked and untouched.
- **Explorer extension.** `DoorManShell.dll` is a native C++ shell extension (`IShellFolder2`). The
  `desktop.ini` of a locked folder points to it, so when Explorer tries to open the folder the extension cancels the
  navigation and asks for the password instead. Once unlocked, it forwards everything to the regular file system folder.
  Quick Access entries are refreshed when locking so they also go through the extension.
- **Relocking.** While a folder is unlocked, its key is stored protected with DPAPI (readable only by your Windows user)
  in `%LOCALAPPDATA%\DoorMan`. It is deleted when the folder is locked again.
- **Context menu.** Menu entries are filtered per folder with `AppliesTo` on `System.ParsingPath` (the real path, not the
  localized display path). DoorMan never changes the default double-click action of folders.

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

Requires the .NET 10 SDK and the Visual Studio C++ build tools.

```powershell
dotnet test tests\DoorMan.Tests   # unit tests
.\publicar.ps1                    # icons, native extension and publish\DoorMan.exe
```

Command-line arguments: `--proteger`, `--abrir`, `--rebloquear` + folder path; `--instalar`, `--desinstalar`,
`--registrar`, `--desregistrar`, `--inicio`, `--agente`.
