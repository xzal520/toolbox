// Windows 11 一级右键菜单扩展（原生 COM，无 ATL 依赖）
// 编译：cl /LD /EHsc /O2 /DUNICODE /D_UNICODE UninstallCommand.cpp /link /DEF:exports.def ole32.lib shell32.lib shlwapi.lib user32.lib

#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <shlobj.h>
#include <new>
#include <string>

// {C1A9B7E4-5D28-4F63-8B90-7A2D6E4C1F58}
static const CLSID CLSID_UninstallCommand =
{ 0xC1A9B7E4, 0x5D28, 0x4F63, { 0x8B, 0x90, 0x7A, 0x2D, 0x6E, 0x4C, 0x1F, 0x58 } };

static HINSTANCE g_hInst = nullptr;
static LONG g_objectCount = 0;

// ── 工具函数 ─────────────────────────────────────────────

static std::wstring ModuleDir()
{
    wchar_t path[MAX_PATH * 2] = {};
    DWORD n = GetModuleFileNameW(g_hInst, path, ARRAYSIZE(path));
    std::wstring full(path, n);
    size_t slash = full.find_last_of(L'\\');
    return slash == std::wstring::npos ? std::wstring() : full.substr(0, slash + 1);
}

// 临时诊断：确认资源管理器是否真的调用了本组件
static void Trace(const wchar_t* message)
{
    std::wstring path = ModuleDir() + L"shell-ext-trace.log";
    FILE* file = nullptr;
    if (_wfopen_s(&file, path.c_str(), L"a, ccs=UTF-8") == 0 && file)
    {
        fwprintf(file, L"%s\n", message);
        fclose(file);
    }
}

static std::wstring HostExe()
{
    const std::wstring dir = ModuleDir();
    const wchar_t* names[] = { L"实用工具箱.exe", L"实用工具箱 轻量版.exe" };
    for (const wchar_t* name : names)
    {
        std::wstring candidate = dir + name;
        if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES) return candidate;
    }
    return std::wstring();
}

static std::wstring FirstPath(IShellItemArray* items)
{
    if (!items) return std::wstring();

    DWORD count = 0;
    if (FAILED(items->GetCount(&count)) || count == 0) return std::wstring();

    IShellItem* item = nullptr;
    if (FAILED(items->GetItemAt(0, &item)) || !item) return std::wstring();

    std::wstring result;
    LPWSTR name = nullptr;
    if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &name)) && name)
    {
        result.assign(name);
        CoTaskMemFree(name);
    }
    item->Release();
    return result;
}

static HRESULT AllocString(const std::wstring& value, LPWSTR* out)
{
    if (!out) return E_POINTER;
    size_t bytes = (value.size() + 1) * sizeof(wchar_t);
    *out = static_cast<LPWSTR>(CoTaskMemAlloc(bytes));
    if (!*out) return E_OUTOFMEMORY;
    memcpy(*out, value.c_str(), bytes);
    return S_OK;
}

static std::wstring FileStem(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    std::wstring name = slash == std::wstring::npos ? path : path.substr(slash + 1);
    size_t dot = name.find_last_of(L'.');
    return dot == std::wstring::npos ? name : name.substr(0, dot);
}

// ── IExplorerCommand 实现 ────────────────────────────────

class UninstallCommand : public IExplorerCommand
{
    LONG _ref;

public:
    UninstallCommand() : _ref(1) { InterlockedIncrement(&g_objectCount); }
    virtual ~UninstallCommand() { InterlockedDecrement(&g_objectCount); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG count = InterlockedDecrement(&_ref);
        if (count == 0) delete this;
        return count;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) override
    {
        DWORD count = 0;
        if (items) items->GetCount(&count);

        std::wstring title;
        if (count > 1)
        {
            title = L"用实用工具箱卸载这 " + std::to_wstring(count) + L" 个";
        }
        else
        {
            std::wstring path = FirstPath(items);
            title = path.empty() ? L"用实用工具箱卸载" : (L"卸载「" + FileStem(path) + L"」");
        }
        return AllocString(title, name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        std::wstring host = HostExe();
        return AllocString(host.empty() ? std::wstring() : host + L",0", icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        return AllocString(L"打开实用工具箱，定位到该程序并卸载", tip);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* guid) override
    {
        if (!guid) return E_POINTER;
        *guid = CLSID_UninstallCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        // 只对文件系统对象显示，回收站/控制面板这类没有路径的位置隐藏
        std::wstring path = FirstPath(items);
        *state = path.empty() ? ECS_HIDDEN : ECS_ENABLED;
        Trace(path.empty() ? L"GetState -> HIDDEN (path empty)"
                           : (L"GetState -> ENABLED " + path).c_str());
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        std::wstring host = HostExe();
        std::wstring target = FirstPath(items);
        if (host.empty())
        {
            MessageBoxW(nullptr,
                L"找不到「实用工具箱.exe」，请把它与本组件放在同一目录后重装右键菜单。",
                L"实用工具箱", MB_OK | MB_ICONWARNING);
            return S_OK;
        }
        if (target.empty()) return S_OK;

        std::wstring args = L"--target \"" + target + L"\"";
        std::wstring workDir = ModuleDir();
        ShellExecuteW(nullptr, L"open", host.c_str(), args.c_str(),
            workDir.empty() ? nullptr : workDir.c_str(), SW_SHOWNORMAL);
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands) *commands = nullptr;
        return E_NOTIMPL;
    }
};

// ── IClassFactory ────────────────────────────────────────

class ClassFactory : public IClassFactory
{
    LONG _ref;

public:
    ClassFactory() : _ref(1) { InterlockedIncrement(&g_objectCount); }
    virtual ~ClassFactory() { InterlockedDecrement(&g_objectCount); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG count = InterlockedDecrement(&_ref);
        if (count == 0) delete this;
        return count;
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        UninstallCommand* command = new (std::nothrow) UninstallCommand();
        if (!command) return E_OUTOFMEMORY;
        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objectCount);
        else InterlockedDecrement(&g_objectCount);
        return S_OK;
    }
};

// ── 导出 ─────────────────────────────────────────────────

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    Trace(L"DllGetClassObject 被调用");
    if (!IsEqualCLSID(rclsid, CLSID_UninstallCommand)) return CLASS_E_CLASSNOTAVAILABLE;
    ClassFactory* factory = new (std::nothrow) ClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
