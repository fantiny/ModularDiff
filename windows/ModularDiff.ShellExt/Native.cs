using System.Runtime.InteropServices;

namespace ModularDiff.ShellExt;

internal static unsafe class Native
{
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_POINTER = unchecked((int)0x80004003), E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOINTERFACE = unchecked((int)0x80004002), E_FAIL = unchecked((int)0x80004005);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int CLASS_E_NO_AGGREGATION = unchecked((int)0x80040110);

    public const uint CF_HDROP = 15;
    public const uint TYMED_HGLOBAL = 1;
    public const uint CMF_DEFAULTONLY = 0x00000001;
    public const uint MIIM_ID = 0x00000002, MIIM_STRING = 0x00000040, MIIM_FTYPE = 0x00000100;
    public const uint MF_STRING = 0x00000000, MF_SEPARATOR = 0x00000800, MF_BYPOSITION = 0x00000400;
    public const int SW_SHOWNORMAL = 1;
    public const uint GCS_VERBW = 0x0004, GCS_HELPTEXTW = 0x0005;

    [StructLayout(LayoutKind.Sequential)]
    public struct FormatEtc
    {
        public ushort cfFormat;
        public void* ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StgMedium
    {
        public uint tymed;
        public void* hGlobal;
        public void* pUnkForRelease;
    }

    // IUnknown + 10 methods; only GetData (index 3) is used.
    [StructLayout(LayoutKind.Sequential)]
    public struct DataObjectVtbl
    {
        public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
        public delegate* unmanaged[Stdcall]<void*, uint> Release;
        public delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int> GetData;
        public delegate* unmanaged[Stdcall]<void*, FormatEtc*, StgMedium*, int> GetDataHere;
        public delegate* unmanaged[Stdcall]<void*, FormatEtc*, int> QueryGetData;
        private nint _unused1; private nint _unused2; private nint _unused3; private nint _unused4; private nint _unused5; private nint _unused6; private nint _unused7;
    }

    public static DataObjectVtbl* DataObject(void* obj) => (DataObjectVtbl*)*(void***)obj;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MenuItemInfo
    {
        public uint cbSize, fMask, fType, fState;
        public uint wID;
        public nint hSubMenu, hbmpChecked, hbmpUnchecked;
        public nuint dwItemData;
        public char* dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ShellExecuteInfo
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public char* lpVerb;
        public char* lpFile;
        public char* lpParameters;
        public char* lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public char* lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconMonitor;
        public nint hProcess;
    }

    // Field order and offsets follow CMINVOKECOMMANDINFOEX exactly (shobjidl_core.h):
    // cbSize(0) fMask(4) hwnd(8) lpVerb(16) lpParameters(24) lpDirectory(32) nShow(40)
    // dwHotKey(44) hIcon(48) lpTitle(56) lpVerbW(64) lpParametersW(72) lpDirectoryW(80) lpTitleW(88).
    [StructLayout(LayoutKind.Sequential)]
    public struct CmInvokeCommandInfo
    {
        public uint cbSize, fMask;
        public nint hwnd;
        public byte* lpVerb; // LPCSTR: MAKEINTRESOURCE(offset) when the user clicks a menu item; canonical verb string otherwise
        public byte* lpParameters, lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
        public byte* lpTitle;
        public char* lpVerbW; // EX tail (offset 64); only when cbSize covers it
        public char* lpParametersW, lpDirectoryW, lpTitleW;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(void* hDrop, uint iFile, char* lpszFile, uint cch);
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, void* item1, void* item2);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool ShellExecuteExW(ShellExecuteInfo* info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFileAttributesW(string lpFileName);
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(void* pv);
    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(StgMedium* medium);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool InsertMenuItemW(nint hMenu, uint item, bool byPosition, MenuItemInfo* itemInfo);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    public const uint CMIC_MASK_FLAG_NO_UI = 0x00000400;
    public const uint MB_ICONINFORMATION = 0x00000040;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int lstrlenW(char* lpString);
    [DllImport("kernel32.dll")]
    public static extern nuint VirtualQuery(void* address, MemoryBasicInformation* buffer, nuint length);
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State, Protect, Type;
    }

    /// Reads up to <paramref name="max"/> characters from a string pointer, returning null when the
    /// pointer is not backed by committed readable memory (garbage verb pointers must not crash Explorer).
    public static unsafe string? TryReadString(char* pointer, int max)
    {
        if (pointer is null || (nint)pointer < 0x10000) return null;
        var info = new MemoryBasicInformation();
        if (VirtualQuery(pointer, &info, (nuint)sizeof(MemoryBasicInformation)) == 0 || info.State != 0x1000 /* MEM_COMMIT */) return null;
        var span = new Span<char>(pointer, max);
        int length = span.IndexOf('\0');
        return length >= 0 ? new string(span[..length]) : null;
    }
    public static unsafe string? TryReadString(byte* pointer, int max)
    {
        if (pointer is null || (nint)pointer < 0x10000) return null;
        var info = new MemoryBasicInformation();
        if (VirtualQuery(pointer, &info, (nuint)sizeof(MemoryBasicInformation)) == 0 || info.State != 0x1000) return null;
        var bytes = new Span<byte>(pointer, max);
        int length = bytes.IndexOf((byte)0);
        return length >= 0 ? System.Text.Encoding.ASCII.GetString(bytes[..length]) : null;
    }
}
