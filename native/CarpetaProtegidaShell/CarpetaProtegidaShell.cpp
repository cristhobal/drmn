// Extensión de shell de Carpeta Protegida.
//
// Las carpetas bloqueadas llevan en su desktop.ini «CLSID={B7E3A0C4-…}», así que el Explorador
// las abre a través de esta clase (igual que hace con C:\Windows\Fonts). Mientras la carpeta está
// bloqueada no expone ningún contenido y, cuando el Explorador intenta mostrarla (CreateViewObject),
// cancela la navegación y lanza CarpetaProtegida.exe para pedir la contraseña. Si la carpeta ya no
// está bloqueada, todo se delega en la carpeta normal del sistema de archivos.
//
// Se mantiene deliberadamente mínima: corre dentro de explorer.exe, y toda la interfaz y el
// cifrado viven en el ejecutable.

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <new>
#include <string>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

// {B7E3A0C4-5F1D-4C2B-9E8A-3D6F0A1C7B25}. Las compilaciones de prueba usan otro (build.ps1 -Clsid) para
// poder cargarlas sin reiniciar el Explorador, que mantiene en memoria la versión ya cargada.
#ifndef LOCKED_CLSID
#define LOCKED_CLSID { 0xb7e3a0c4, 0x5f1d, 0x4c2b, { 0x9e, 0x8a, 0x3d, 0x6f, 0x0a, 0x1c, 0x7b, 0x25 } }
#endif
static const CLSID CLSID_LockedFolder = LOCKED_CLSID;
// CLSID_ShellFSFolder: la carpeta normal del sistema de archivos.
static const CLSID CLSID_FileSystemFolder = { 0xf3364ba0, 0x65b9, 0x11ce, { 0xa9, 0xba, 0x00, 0xaa, 0x00, 0x4a, 0xe8, 0x37 } };

static const wchar_t VaultFileName[] = L"Desbloquear carpeta.cpbloq";
static const wchar_t ExeFileName[] = L"CarpetaProtegida.exe";

static HMODULE g_module;
static LONG g_objects;
static LONG g_locks;

// Diagnóstico opcional: solo escribe si existe %LOCALAPPDATA%\CarpetaProtegida\shell.log.
static void Trace(const wchar_t* format, ...)
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH);
    if (!n || n >= MAX_PATH || !PathAppendW(path, L"CarpetaProtegida\\shell.log"))
        return;
    HANDLE file = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE)
        return;
    wchar_t text[1024];
    va_list args;
    va_start(args, format);
    int len = _vsnwprintf_s(text, _TRUNCATE, format, args);
    va_end(args);
    if (len > 0)
    {
        char utf8[3072];
        int bytes = WideCharToMultiByte(CP_UTF8, 0, text, len, utf8, sizeof(utf8) - 2, nullptr, nullptr);
        utf8[bytes++] = '\r';
        utf8[bytes++] = '\n';
        DWORD written;
        WriteFile(file, utf8, bytes, &written, nullptr);
    }
    CloseHandle(file);
}

static void TraceIid(const wchar_t* what, REFIID riid)
{
    wchar_t iid[64];
    StringFromGUID2(riid, iid, 64);
    Trace(L"%u %s %s", GetCurrentProcessId(), what, iid);
}

// El Explorador puede pedir la vista dos veces seguidas para la misma navegación.
static SRWLOCK g_lastLock = SRWLOCK_INIT;
static std::wstring g_lastPath;
static ULONGLONG g_lastTick;

static void LaunchUnlock(const std::wstring& folder, HWND owner)
{
    ULONGLONG now = GetTickCount64();
    AcquireSRWLockExclusive(&g_lastLock);
    bool duplicate = now - g_lastTick < 1500 && _wcsicmp(g_lastPath.c_str(), folder.c_str()) == 0;
    if (!duplicate)
    {
        g_lastPath = folder;
        g_lastTick = now;
    }
    ReleaseSRWLockExclusive(&g_lastLock);
    if (duplicate)
        return;

    wchar_t exe[MAX_PATH];
    if (!GetModuleFileNameW(g_module, exe, MAX_PATH) || !PathRemoveFileSpecW(exe) || !PathAppendW(exe, ExeFileName))
        return;

    HWND window = owner ? GetAncestor(owner, GA_ROOT) : nullptr;
    std::wstring command = L"\"" + std::wstring(exe) + L"\" --abrir \"" + folder + L"\" --ventana " +
        std::to_wstring(reinterpret_cast<ULONG_PTR>(window));

    // El diálogo de contraseña debe poder ponerse delante del Explorador.
    AllowSetForegroundWindow(ASFW_ANY);
    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi = {};
    if (CreateProcessW(exe, &command[0], nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
    {
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }
}

class LockedFolder final : public IShellFolder2, public IPersistFolder3
{
    LONG _refs = 1;
    PIDLIST_ABSOLUTE _pidl = nullptr;
    std::wstring _path;
    IShellFolder2* _inner = nullptr;

    ~LockedFolder()
    {
        if (_inner)
            _inner->Release();
        CoTaskMemFree(_pidl);
        InterlockedDecrement(&g_objects);
    }

    bool IsLocked() const
    {
        if (_path.empty())
            return true;
        std::wstring vault = _path;
        if (vault.back() != L'\\')
            vault += L'\\';
        vault += VaultFileName;
        DWORD attributes = GetFileAttributesW(vault.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
    }

    // La carpeta del sistema de archivos que hay debajo, para cuando ya está desbloqueada.
    IShellFolder2* Inner()
    {
        if (_inner || !_pidl)
            return _inner;
        IPersistFolder3* persist = nullptr;
        if (FAILED(CoCreateInstance(CLSID_FileSystemFolder, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&persist))))
            return nullptr;
        PERSIST_FOLDER_TARGET_INFO target = {};
        target.pidlTargetFolder = _pidl;
        lstrcpynW(target.szTargetParsingName, _path.c_str(), MAX_PATH);
        target.dwAttributes = FILE_ATTRIBUTE_DIRECTORY;
        target.csidl = -1;
        if (SUCCEEDED(persist->InitializeEx(nullptr, _pidl, &target)))
            persist->QueryInterface(IID_PPV_ARGS(&_inner));
        persist->Release();
        return _inner;
    }

    HRESULT SetLocation(PCIDLIST_ABSOLUTE pidl, const wchar_t* path)
    {
        if (!pidl)
            return E_INVALIDARG;
        PIDLIST_ABSOLUTE copy = ILCloneFull(pidl);
        if (!copy)
            return E_OUTOFMEMORY;
        CoTaskMemFree(_pidl);
        _pidl = copy;
        if (_inner)
        {
            _inner->Release();
            _inner = nullptr;
        }
        wchar_t buffer[MAX_PATH];
        if (path && *path)
            _path = path;
        else if (SHGetPathFromIDListW(pidl, buffer))
            _path = buffer;
        else
            _path.clear();
        Trace(L"%u Initialize path=[%s] locked=%d", GetCurrentProcessId(), _path.c_str(), IsLocked() ? 1 : 0);
        return S_OK;
    }

#define FORWARD_IF_UNLOCKED(call)                           \
    if (!IsLocked())                                        \
    {                                                       \
        IShellFolder2* inner = Inner();                     \
        return inner ? inner->call : E_FAIL;                \
    }

public:
    LockedFolder() { InterlockedIncrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IShellFolder || riid == IID_IShellFolder2)
            *ppv = static_cast<IShellFolder2*>(this);
        else if (riid == IID_IPersist || riid == IID_IPersistFolder || riid == IID_IPersistFolder2 || riid == IID_IPersistFolder3)
            *ppv = static_cast<IPersistFolder3*>(this);
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG refs = InterlockedDecrement(&_refs);
        if (refs == 0)
            delete this;
        return refs;
    }

    // IPersist / IPersistFolder / IPersistFolder2 / IPersistFolder3
    IFACEMETHODIMP GetClassID(CLSID* clsid) override
    {
        if (!clsid)
            return E_POINTER;
        *clsid = CLSID_LockedFolder;
        return S_OK;
    }
    IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE pidl) override { return SetLocation(pidl, nullptr); }
    IFACEMETHODIMP GetCurFolder(PIDLIST_ABSOLUTE* pidl) override
    {
        if (!pidl)
            return E_POINTER;
        *pidl = _pidl ? ILCloneFull(_pidl) : nullptr;
        return *pidl ? S_OK : E_FAIL;
    }
    IFACEMETHODIMP InitializeEx(IBindCtx*, PCIDLIST_ABSOLUTE root, const PERSIST_FOLDER_TARGET_INFO* target) override
    {
        return SetLocation(root, target ? target->szTargetParsingName : nullptr);
    }
    IFACEMETHODIMP GetFolderTargetInfo(PERSIST_FOLDER_TARGET_INFO*) override { return E_NOTIMPL; }

    // IShellFolder
    IFACEMETHODIMP ParseDisplayName(HWND hwnd, IBindCtx* pbc, LPWSTR name, ULONG* eaten, PIDLIST_RELATIVE* pidl, ULONG* attributes) override
    {
        FORWARD_IF_UNLOCKED(ParseDisplayName(hwnd, pbc, name, eaten, pidl, attributes))
        if (pidl)
            *pidl = nullptr;
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    IFACEMETHODIMP EnumObjects(HWND hwnd, SHCONTF flags, IEnumIDList** items) override
    {
        FORWARD_IF_UNLOCKED(EnumObjects(hwnd, flags, items))
        if (!items)
            return E_POINTER;
        *items = nullptr;
        return S_FALSE; // Bloqueada: no hay nada que mostrar (miniaturas, panel de navegación…).
    }
    IFACEMETHODIMP BindToObject(PCUIDLIST_RELATIVE pidl, IBindCtx* pbc, REFIID riid, void** ppv) override
    {
        FORWARD_IF_UNLOCKED(BindToObject(pidl, pbc, riid, ppv))
        if (ppv)
            *ppv = nullptr;
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    IFACEMETHODIMP BindToStorage(PCUIDLIST_RELATIVE pidl, IBindCtx* pbc, REFIID riid, void** ppv) override
    {
        FORWARD_IF_UNLOCKED(BindToStorage(pidl, pbc, riid, ppv))
        if (ppv)
            *ppv = nullptr;
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }
    IFACEMETHODIMP CompareIDs(LPARAM column, PCUIDLIST_RELATIVE a, PCUIDLIST_RELATIVE b) override
    {
        FORWARD_IF_UNLOCKED(CompareIDs(column, a, b))
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
    }
    IFACEMETHODIMP CreateViewObject(HWND owner, REFIID riid, void** ppv) override
    {
        TraceIid(L"CreateViewObject", riid);
        FORWARD_IF_UNLOCKED(CreateViewObject(owner, riid, ppv))
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IShellView)
        {
            // El Explorador quiere mostrar la carpeta: no se entra; se pide la contraseña.
            LaunchUnlock(_path, owner);
            return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        }
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP GetAttributesOf(UINT count, PCUITEMID_CHILD_ARRAY items, SFGAOF* attributes) override
    {
        FORWARD_IF_UNLOCKED(GetAttributesOf(count, items, attributes))
        if (!attributes)
            return E_POINTER;
        if (count != 0)
            return E_INVALIDARG;
        *attributes &= SFGAO_FOLDER | SFGAO_FILESYSANCESTOR | SFGAO_FILESYSTEM | SFGAO_STORAGEANCESTOR;
        return S_OK;
    }
    IFACEMETHODIMP GetUIObjectOf(HWND owner, UINT count, PCUITEMID_CHILD_ARRAY items, REFIID riid, UINT* reserved, void** ppv) override
    {
        FORWARD_IF_UNLOCKED(GetUIObjectOf(owner, count, items, riid, reserved, ppv))
        if (ppv)
            *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP GetDisplayNameOf(PCUITEMID_CHILD pidl, SHGDNF flags, STRRET* name) override
    {
        FORWARD_IF_UNLOCKED(GetDisplayNameOf(pidl, flags, name))
        return E_INVALIDARG;
    }
    IFACEMETHODIMP SetNameOf(HWND owner, PCUITEMID_CHILD pidl, LPCWSTR name, SHGDNF flags, PITEMID_CHILD* out) override
    {
        FORWARD_IF_UNLOCKED(SetNameOf(owner, pidl, name, flags, out))
        if (out)
            *out = nullptr;
        return E_ACCESSDENIED;
    }

    // IShellFolder2
    IFACEMETHODIMP GetDefaultSearchGUID(GUID* guid) override
    {
        FORWARD_IF_UNLOCKED(GetDefaultSearchGUID(guid))
        return E_NOTIMPL;
    }
    IFACEMETHODIMP EnumSearches(IEnumExtraSearch** searches) override
    {
        FORWARD_IF_UNLOCKED(EnumSearches(searches))
        if (searches)
            *searches = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetDefaultColumn(DWORD reserved, ULONG* sort, ULONG* display) override
    {
        FORWARD_IF_UNLOCKED(GetDefaultColumn(reserved, sort, display))
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetDefaultColumnState(UINT column, SHCOLSTATEF* flags) override
    {
        FORWARD_IF_UNLOCKED(GetDefaultColumnState(column, flags))
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetDetailsEx(PCUITEMID_CHILD pidl, const SHCOLUMNID* column, VARIANT* value) override
    {
        FORWARD_IF_UNLOCKED(GetDetailsEx(pidl, column, value))
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetDetailsOf(PCUITEMID_CHILD pidl, UINT column, SHELLDETAILS* details) override
    {
        FORWARD_IF_UNLOCKED(GetDetailsOf(pidl, column, details))
        return E_NOTIMPL;
    }
    IFACEMETHODIMP MapColumnToSCID(UINT column, SHCOLUMNID* scid) override
    {
        FORWARD_IF_UNLOCKED(MapColumnToSCID(column, scid))
        return E_NOTIMPL;
    }

#undef FORWARD_IF_UNLOCKED
};

class ClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    // Objeto estático: la vida de la DLL la controlan g_objects y g_locks.
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv)
            return E_POINTER;
        *ppv = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;
        auto* folder = new (std::nothrow) LockedFolder();
        if (!folder)
            return E_OUTOFMEMORY;
        HRESULT hr = folder->QueryInterface(riid, ppv);
        folder->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
            InterlockedIncrement(&g_locks);
        else
            InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static ClassFactory g_factory;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    *ppv = nullptr;
    if (clsid != CLSID_LockedFolder)
        return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}
