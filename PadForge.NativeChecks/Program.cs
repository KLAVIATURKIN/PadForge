using System.Reflection;
using System.Runtime.InteropServices;
using SDL3;
using PadForge.Engine;

namespace PadForge.NativeChecks;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Enumerate(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Free(IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Malloc(nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Calloc(nuint count, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Realloc(IntPtr pointer, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GetMemory(out IntPtr malloc, out IntPtr calloc, out IntPtr realloc, out IntPtr free);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte SetMemory(IntPtr malloc, IntPtr calloc, IntPtr realloc, IntPtr free);

    static T Export<T>(IntPtr module, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 2, "Expected the SDL library path and enumeration name.");
            string symbol = args[1];
            Require(symbol is "SDL_GetJoysticks" or "SDL_GetKeyboards" or "SDL_GetMice"
                or "buttons-mapped" or "buttons-unmapped" or "buttons-all-mapped" or "buttons-joystick", "Unsupported check.");
            IntPtr module = NativeLibrary.Load(Path.GetFullPath(args[0]));
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly,
                    (name, _, _) => name == "SDL3" ? module : IntPtr.Zero);
                if (symbol.StartsWith("buttons-", StringComparison.Ordinal)) CheckButtons(symbol);
                else Check(module, symbol);
            }
            finally { NativeLibrary.Free(module); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetBaseException().Message);
            return 1;
        }
    }

    static void CheckButtons(string scenario)
    {
        using var wrapper = new SdlDeviceWrapper();
        void Set(string name, object value) => typeof(SdlDeviceWrapper).GetProperty(name)!.SetValue(wrapper, value);
        bool gamepad = scenario != "buttons-joystick";
        var mapped = scenario == "buttons-unmapped" ? new HashSet<int>()
            : scenario == "buttons-all-mapped" ? new HashSet<int> { 22, 23 } : new HashSet<int> { 22 };
        try
        {
            // SDL rejects this unregistered gamepad pointer before dereferencing it.
            Set("GameController", gamepad ? new IntPtr(1) : IntPtr.Zero);
            Set("NumButtons", gamepad ? 22 : 24);
            Set("RawButtonCount", 24);
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(SdlDeviceWrapper).GetField("_mappedRawButtonIndices", hidden)!.SetValue(wrapper, mapped);
            var supported = (int[])typeof(SdlDeviceWrapper).GetMethod("ComputeSupportedButtonIndices", hidden)!.Invoke(wrapper, null)!;
            var objects = wrapper.GetDeviceObjects().Where(x => x.IsButton).Select(x => x.InputIndex).ToArray();
            int[] expected = gamepad ? Enumerable.Range(22, 2).Where(x => !mapped.Contains(x)).ToArray() : Enumerable.Range(0, 24).ToArray();
            Console.WriteLine($"{scenario}: supported=[{string.Join(',', supported)}] objects=[{string.Join(',', objects)}]");
            Require(supported.SequenceEqual(expected), "The supported-button control did not match the intended layout.");
            Require(objects.SequenceEqual(expected), "Device objects exposed consumed raw buttons.");
        }
        finally { Set("GameController", IntPtr.Zero); }
    }

    static void Check(IntPtr module, string symbol)
    {
        // This process never initializes an SDL subsystem or opens a device.
        var enumerate = Export<Enumerate>(module, symbol);
        var free = Export<Free>(module, "SDL_free");
        IntPtr pointer = enumerate(out int count);
        free(pointer);
        Require(count == 0, "The enumeration was not empty before subsystem initialization.");
        var get = Export<GetMemory>(module, "SDL_GetMemoryFunctions");
        var set = Export<SetMemory>(module, "SDL_SetMemoryFunctions");
        get(out var m, out var c, out var r, out var f);
        var malloc = Marshal.GetDelegateForFunctionPointer<Malloc>(m);
        var calloc = Marshal.GetDelegateForFunctionPointer<Calloc>(c);
        var realloc = Marshal.GetDelegateForFunctionPointer<Realloc>(r);
        var release = Marshal.GetDelegateForFunctionPointer<Free>(f);
        var outstanding = new HashSet<IntPtr>();
        int allocations = 0;
        Malloc trackMalloc = size =>
        {
            allocations++;
            var value = malloc(size);
            if (value != IntPtr.Zero) outstanding.Add(value);
            return value;
        };
        Calloc trackCalloc = (items, size) =>
        {
            var value = calloc(items, size);
            if (value != IntPtr.Zero) outstanding.Add(value);
            return value;
        };
        Realloc trackRealloc = (old, size) =>
        {
            var value = realloc(old, size);
            if (value != IntPtr.Zero) { outstanding.Remove(old); outstanding.Add(value); }
            return value;
        };
        Free trackFree = value => { outstanding.Remove(value); release(value); };
        Require(set(Marshal.GetFunctionPointerForDelegate(trackMalloc), Marshal.GetFunctionPointerForDelegate(trackCalloc),
            Marshal.GetFunctionPointerForDelegate(trackRealloc), Marshal.GetFunctionPointerForDelegate(trackFree)) != 0,
            "The isolated allocator hook was rejected.");
        try
        {
            pointer = enumerate(out _);
            Require(outstanding.Count == 1, "The native allocation control did not fire.");
            free(pointer);
            Require(outstanding.Count == 0, "The native free control did not fire.");
            var wrapper = typeof(SDL).GetMethod(symbol, Type.EmptyTypes)!;
            for (int i = 0; i < 5; i++)
                Require(((uint[])wrapper.Invoke(null, null)!).Length == 0, "The managed result was not empty.");
            Console.WriteLine($"{symbol}: nativeControl=0 wrapperCalls=5 allocations={allocations} outstanding={outstanding.Count}");
            Require(allocations == 6, "The wrapper did not use the instrumented module.");
            Require(outstanding.Count == 0, "An empty native array was not freed.");
        }
        finally
        {
            set(m, c, r, f);
            foreach (var leaked in outstanding) release(leaked);
            GC.KeepAlive(trackMalloc);
            GC.KeepAlive(trackCalloc);
            GC.KeepAlive(trackRealloc);
            GC.KeepAlive(trackFree);
        }
    }
}
