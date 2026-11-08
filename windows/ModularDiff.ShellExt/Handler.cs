using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModularDiff.ShellExt;

// Layout handed to Explorer: [IShellExtInit vtable slot][IContextMenu vtable slot][GCHandle][refcount].
// The IShellExtInit* points at the first slot; the IContextMenu* at the second, so each side derives
// the object base with a fixed offset. Managed state rides in the GCHandle.
internal static unsafe class Handler
{
    private const string StateKey = @"Software\ModularDiff\Explorer";

    internal sealed class State
    {
        public List<SelectionPath> Selection = [];
        public SelectionPath? Left;
        public string? GuiExePath;
        public MenuEntry[] Plan = [];
        public uint MenuBase; // idCmdFirst of the most recent QueryContextMenu build
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandlerNative
    {
        public void* InitVtbl;
        public void* MenuVtbl;
        public nint GcHandle;
        public int RefCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtInitVtbl
    {
        public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
        public delegate* unmanaged[Stdcall]<void*, uint> Release;
        public delegate* unmanaged[Stdcall]<void*, void*, void*, void*, int> Initialize; // this, pidlFolder, dataObj, progIdKey
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ContextMenuVtbl
    {
        public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
        public delegate* unmanaged[Stdcall]<void*, uint> Release;
        public delegate* unmanaged[Stdcall]<void*, nint, uint, uint, uint, uint, int> QueryContextMenu;
        public delegate* unmanaged[Stdcall]<void*, Native.CmInvokeCommandInfo*, int> InvokeCommand;
        public delegate* unmanaged[Stdcall]<void*, uint, uint, uint*, char*, uint, int> GetCommandString;
    }

    private static readonly ExtInitVtbl* InitTable = CreateTable(() => new ExtInitVtbl
    {
        QueryInterface = &QueryInterfaceInit, AddRef = &AddRefInit, Release = &ReleaseInit,
        Initialize = &Initialize,
    });
    private static readonly ContextMenuVtbl* MenuTable = CreateTable(() => new ContextMenuVtbl
    {
        QueryInterface = &QueryInterfaceMenu, AddRef = &AddRefMenu, Release = &ReleaseMenu,
        QueryContextMenu = &QueryContextMenu, InvokeCommand = &InvokeCommand, GetCommandString = &GetCommandString,
    });
    private static T* CreateTable<T>(Func<T> fill) where T : unmanaged
    {
        var table = (T*)NativeMemory.Alloc((nuint)sizeof(T));
        *table = fill();
        return table;
    }

    public static int FactoryQueryInterface(void* self, Guid* riid, void** ppv)
    {
        if (ppv is null) return Native.E_POINTER;
        *ppv = null;
        if (riid is null || (*riid != Com.IidIClassFactory && *riid != Com.IidIUnknown)) return Native.E_NOINTERFACE;
        *ppv = self;
        return Native.S_OK;
    }

    // Creates a handler (refcount 1) and hands out the requested interface, keeping exactly one reference.
    public static int QueryInterfaceNewHandler(Guid* riid, void** ppv)
    {
        var native = (HandlerNative*)NativeMemory.Alloc((nuint)sizeof(HandlerNative));
        native->InitVtbl = InitTable;
        native->MenuVtbl = MenuTable;
        native->RefCount = 1;
        native->GcHandle = GCHandle.ToIntPtr(GCHandle.Alloc(new State()));
        int hr = QueryInterfaceImpl(native, true, riid, ppv);
        if (hr != Native.S_OK)
        {
            GCHandle.FromIntPtr(native->GcHandle).Free();
            NativeMemory.Free(native);
        }
        else native->RefCount--; // QI added a second reference; the caller owns the initial one
        return hr;
    }

    private static HandlerNative* Base(void* self, bool fromInit) =>
        (HandlerNative*)((byte*)self - (fromInit ? 0 : sizeof(void*)));
    private static State StateOf(HandlerNative* native) =>
        (State)GCHandle.FromIntPtr(native->GcHandle).Target!;

    private static int QueryInterfaceImpl(void* self, bool fromInit, Guid* riid, void** ppv)
    {
        if (ppv is null) return Native.E_POINTER;
        *ppv = null;
        if (riid is null) return Native.E_INVALIDARG;
        HandlerNative* native = Base(self, fromInit);
        if (*riid == Com.IidIUnknown || *riid == Com.IidIShellExtInit) *ppv = &native->InitVtbl;
        else if (*riid == Com.IidIContextMenu) *ppv = &native->MenuVtbl;
        else return Native.E_NOINTERFACE;
        native->RefCount++;
        return Native.S_OK;
    }
    private static uint AddRefImpl(void* self, bool fromInit) => (uint)++Base(self, fromInit)->RefCount;
    private static uint ReleaseImpl(void* self, bool fromInit)
    {
        HandlerNative* native = Base(self, fromInit);
        if (--native->RefCount != 0) return (uint)native->RefCount;
        GCHandle.FromIntPtr(native->GcHandle).Free();
        NativeMemory.Free(native);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int QueryInterfaceInit(void* self, Guid* riid, void** ppv) => QueryInterfaceImpl(self, true, riid, ppv);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint AddRefInit(void* self) => AddRefImpl(self, true);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint ReleaseInit(void* self) => ReleaseImpl(self, true);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static int QueryInterfaceMenu(void* self, Guid* riid, void** ppv) => QueryInterfaceImpl(self, false, riid, ppv);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint AddRefMenu(void* self) => AddRefImpl(self, false);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })] private static uint ReleaseMenu(void* self) => ReleaseImpl(self, false);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Initialize(void* self, void* pidlFolder, void* dataObj, void* progIdKey)
    {
        State state = StateOf(Base(self, true));
        state.Selection.Clear();
        state.Left = null;
        state.GuiExePath = ReadState("GuiExePath");
        if (ReadState("Left") is { } left && Native.GetFileAttributesW(left) != Native.INVALID_FILE_ATTRIBUTES)
            state.Left = new SelectionPath(left, DirectoryExists(left));
        if (dataObj is not null) state.Selection.AddRange(PathsFromDataObject(dataObj));
        state.Plan = [.. MenuPlan.Plan(state.Selection, state.Left)];
        return Native.S_OK;
    }

    private static List<SelectionPath> PathsFromDataObject(void* dataObj)
    {
        var result = new List<SelectionPath>();
        var format = new Native.FormatEtc { cfFormat = (ushort)Native.CF_HDROP, dwAspect = 1, lindex = -1, tymed = Native.TYMED_HGLOBAL };
        Native.StgMedium medium;
        int hr = Native.DataObject(dataObj)->GetData(dataObj, &format, &medium);
        if (hr != Native.S_OK) return result;
        try
        {
            var buffer = new char[1024];
            uint count = Native.DragQueryFileW(medium.hGlobal, 0xFFFFFFFF, null, 0);
            fixed (char* pinned = buffer)
            {
                for (uint i = 0; i < count; i++)
                {
                    uint written = Native.DragQueryFileW(medium.hGlobal, i, pinned, (uint)buffer.Length);
                    if (written == 0) continue;
                    string path = new(pinned, 0, (int)written);
                    result.Add(new SelectionPath(path, DirectoryExists(path)));
                }
            }
        }
        finally { Native.ReleaseStgMedium(&medium); }
        return result;
    }

    private static bool DirectoryExists(string path) =>
        (Native.GetFileAttributesW(path) & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY

    private static string? ReadState(string name) => Registry.GetValue(@"HKEY_CURRENT_USER\" + StateKey, name, null) as string;
    private static void WriteState(string name, string value) => Registry.SetValue(@"HKEY_CURRENT_USER\" + StateKey, name, value);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryContextMenu(void* self, nint hMenu, uint indexMenu, uint idFirst, uint idLast, uint flags)
    {
        State state = StateOf(Base(self, false));
        state.MenuBase = idFirst;
        if ((flags & Native.CMF_DEFAULTONLY) != 0 || state.Plan.Length == 0) return (int)idFirst;
        var separator = new Native.MenuItemInfo { cbSize = (uint)sizeof(Native.MenuItemInfo), fMask = Native.MIIM_FTYPE | Native.MIIM_ID, fType = Native.MF_SEPARATOR, wID = idFirst };
        Native.InsertMenuItemW(hMenu, indexMenu, true, &separator);
        Span<char> text = stackalloc char[512]; // menu texts stay far below this; long names are truncated
        for (int i = 0; i < state.Plan.Length; i++)
        {
            int length = Math.Min(state.Plan[i].Text.Length, text.Length - 1);
            state.Plan[i].Text.AsSpan(0, length).CopyTo(text);
            text[length] = '\0'; // the buffer is reused across items; terminate each string
            fixed (char* pinned = text)
            {
                var item = new Native.MenuItemInfo
                {
                    cbSize = (uint)sizeof(Native.MenuItemInfo),
                    fMask = Native.MIIM_ID | Native.MIIM_STRING,
                    fType = Native.MF_STRING,
                    wID = (uint)(idFirst + i),
                    dwTypeData = pinned,
                    cch = (uint)length,
                };
                Native.InsertMenuItemW(hMenu, indexMenu + 1 + (uint)i, true, &item);
            }
        }
        return (int)(idFirst + (uint)state.Plan.Length);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int InvokeCommand(void* self, Native.CmInvokeCommandInfo* info)
    {
        State state = StateOf(Base(self, false));
        if (info is null) return Native.E_FAIL;
        long verb = (long)info->lpVerb;
        // Per shobjidl_core docs: when the user clicks a menu item the Shell passes
        // MAKEINTRESOURCE(offset) — detected with IS_INTRESOURCE (value >> 16 == 0).
        // Canonical verb strings (ANSI lpVerb, or lpVerbW when cbSize covers the EX tail) are also accepted.
        int index = (verb >> 16) == 0 ? (int)verb : VerbIndex(state, info);
        if (index < 0 || index >= state.Plan.Length) return Native.E_FAIL; // not ours: E_FAIL lets other handlers try
        MenuEntry entry = state.Plan[index];
        if (entry.Action == MenuAction.SelectLeft)
        {
            WriteState("Left", state.Selection[0].Path);
            Native.SHChangeNotify(0x08000000, 0, null, null); // refresh context menus everywhere
            return Native.S_OK;
        }
        SelectionPath left = entry.Action == MenuAction.Compare ? state.Selection[0] : state.Left!;
        SelectionPath right = entry.Action == MenuAction.Compare ? state.Selection[1] : state.Selection[0];
        return Launch(state.GuiExePath, left.Path, right.Path) ? Native.S_OK : Native.E_FAIL;
    }

    private static int VerbIndex(State state, Native.CmInvokeCommandInfo* info)
    {
        string? name = Native.TryReadString(info->lpVerb, 64);
        if (name is null && info->cbSize >= 72) name = Native.TryReadString(info->lpVerbW, 64);
        if (name is null) return -1;
        for (int i = 0; i < state.Plan.Length; i++)
            if (string.Equals(VerbName(state.Plan[i].Action), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string VerbName(MenuAction action) => action switch
    {
        MenuAction.Compare => "ModularDiffCompare",
        MenuAction.SelectLeft => "ModularDiffSelectLeft",
        _ => "ModularDiffCompareTo",
    };

    private static bool Launch(string? exe, string left, string right)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        Span<char> file = stackalloc char[exe!.Length + 1];
        exe.AsSpan().CopyTo(file);
        string arguments = "--left \"" + left + "\" --right \"" + right + "\" --auto-compare";
        Span<char> parameters = stackalloc char[arguments.Length + 1];
        arguments.AsSpan().CopyTo(parameters);
        fixed (char* filePtr = file)
        fixed (char* parameterPtr = parameters)
        {
            var info = new Native.ShellExecuteInfo
            {
                cbSize = (uint)sizeof(Native.ShellExecuteInfo),
                lpFile = filePtr,
                lpParameters = parameterPtr,
                nShow = Native.SW_SHOWNORMAL,
            };
            return Native.ShellExecuteExW(&info);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetCommandString(void* self, uint idCmd, uint type, uint* reserved, char* name, uint cchMax)
    {
        State state = StateOf(Base(self, false));
        if (idCmd >= (uint)state.Plan.Length) return Native.E_FAIL;
        if ((type != Native.GCS_HELPTEXTW && type != Native.GCS_VERBW) || name is null || cchMax == 0) return Native.E_NOINTERFACE;
        string text = type == Native.GCS_VERBW
            ? VerbName(state.Plan[(int)idCmd].Action)
            : state.Plan[(int)idCmd].Action switch
            {
                MenuAction.Compare => "比较选中的两个文件或目录",
                MenuAction.SelectLeft => "记住此项作为比较的左侧",
                _ => "与记住的左侧比较",
            };
        int length = Math.Min(text.Length, (int)cchMax - 1);
        for (int i = 0; i < length; i++) name[i] = text[i];
        name[length] = '\0';
        return Native.S_OK;
    }
}
