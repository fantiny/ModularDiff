using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ModularDiff.ShellExt;

// COM interop notes: objects handed to Explorer are unmanaged blocks whose first field is the
// vtable pointer; managed state is attached through a GCHandle stored next to the vtable slots.
// Vtables themselves live in NativeMemory for the lifetime of the module.
internal static unsafe class Com
{
    public static readonly Guid ClsidMenu = new("144097CE-5032-445B-9864-8F38CB314AF5");
    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IidIClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IidIShellExtInit = new("000214E8-0000-0000-C000-000000000046"); // IID_IShellExtInit
    public static readonly Guid IidIContextMenu = new("000214E4-0000-0000-C000-000000000046");

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DllGetClassObject(Guid* rclsid, Guid* riid, void** ppv)
    {
        if (ppv is null) return Native.E_POINTER;
        *ppv = null;
        if (rclsid is null || riid is null) return Native.E_INVALIDARG;
        if (*rclsid != ClsidMenu) { return Native.CLASS_E_CLASSNOTAVAILABLE; }
        if (*riid != IidIClassFactory && *riid != IidIUnknown) { return Native.E_NOINTERFACE; }
        *ppv = ClassFactory.Instance;
        return Native.S_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int DllCanUnloadNow() => Native.S_FALSE; // keep the module resident; Explorer hosts few instances

    private static class ClassFactory
    {
        private static readonly Vtbl* Table = CreateTable();
        private static Vtbl* CreateTable()
        {
            var table = (Vtbl*)NativeMemory.Alloc((nuint)sizeof(Vtbl));
            *table = new Vtbl
            {
                QueryInterface = &QueryInterface, AddRef = &AddRef, Release = &Release,
                CreateInstance = &CreateInstance, LockServer = &LockServer,
            };
            return table;
        }
        private static void* _slot;
        // Lazy on purpose: static field initializers run in declaration order, so the slot must
        // not be built before Table exists.
        public static void* Instance { get { if (_slot == null) _slot = CreateInstanceSlot(); return _slot; } }
        private static void* CreateInstanceSlot()
        {
            void** slot = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
            *slot = Table;
            return slot;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int QueryInterface(void* self, Guid* riid, void** ppv) => Handler.FactoryQueryInterface(self, riid, ppv);
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint AddRef(void* self) => 2;
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static uint Release(void* self) => 1; // static singleton: never destroyed
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int CreateInstance(void* self, void* outer, Guid* riid, void** ppv)
        {
            if (ppv is null) return Native.E_POINTER;
            *ppv = null;
            if (outer is not null) { return Native.CLASS_E_NO_AGGREGATION; }
            int hr = Handler.QueryInterfaceNewHandler(riid, ppv);
            return hr;
        }
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int LockServer(void* self, int lockServer) => Native.S_OK;

        [StructLayout(LayoutKind.Sequential)]
        public struct Vtbl
        {
            public delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterface;
            public delegate* unmanaged[Stdcall]<void*, uint> AddRef;
            public delegate* unmanaged[Stdcall]<void*, uint> Release;
            public delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, int> CreateInstance;
            public delegate* unmanaged[Stdcall]<void*, int, int> LockServer;
        }
    }
}
