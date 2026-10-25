using System;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.ForceDimension
{
    /// <summary>
    /// Force Dimension's <c>dhd64.dll</c> (issue #521), the calls
    /// <see cref="IFalconLink"/> names, resolved from the copy
    /// <see cref="ForceDimensionCatalog"/> finds.
    ///
    /// <para>The header declares every call <c>__stdcall</c> on Windows
    /// (<c>__SDK</c> in <c>dhdc.h</c>), the type CHAI3D casts each export to
    /// (<c>CDeltaDevices.cpp</c>), and x64 folds it into the one convention.
    /// A device ID is a C <c>char</c>, so it crosses as a signed byte. The
    /// module is loaded once for the process and never freed, the G-key
    /// library's rule: a haptic thread can still be inside a call when the
    /// feature stops. Every export resolves or none does.</para>
    /// </summary>
    public sealed class ForceDimensionLibrary : IFalconLink
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Count();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Id(sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Serial(out ushort serial, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_GetPosition(out double x, out double y, out double z, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint PFN_ButtonMask(sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_EnableForce(byte on, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_SetForce(double x, double y, double z, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Double(double value, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Int(int value, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PFN_Status([Out] int[] status, sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate double PFN_Frequency(sbyte id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr PFN_Text();

        /// <summary><c>DHD_MAX_STATUS</c>.</summary>
        public const int StatusCount = 17;

        private PFN_Count _getDeviceCount, _errorGetLast;
        private PFN_Id _openId, _close, _getSystemType;
        private PFN_Serial _getSerialNumber;
        private PFN_GetPosition _getPosition;
        private PFN_ButtonMask _getButtonMask;
        private PFN_EnableForce _enableForce;
        private PFN_SetForce _setForce;
        private PFN_Double _setMaxForce, _setStandardGravity;
        private PFN_Int _setGravityCompensation;
        private PFN_Status _getStatus;
        private PFN_Frequency _getComFreq;
        private PFN_Text _errorGetLastStr, _getSdkVersionStr;

        /// <summary>Where the library was loaded from.</summary>
        public string Path { get; private set; } = string.Empty;

        private ForceDimensionLibrary() { }

        /// <summary>
        /// The library, or null with <paramref name="reason"/> saying why:
        /// "gate" in the test host, "platform" when this process cannot load
        /// an x64 library, "missing" with no candidate on this PC, "load"
        /// when no candidate loaded, "exports" when a DLL loaded and is not
        /// this library.
        /// </summary>
        public static ForceDimensionLibrary TryLoad(string picked, out string reason)
        {
            reason = "missing";
            if (PadForge.Engine.Common.VendorProgramGate.Refuses)
            {
                reason = "gate";
                return null;
            }
            // Force Dimension ships the library for x86 and x64 Windows only.
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                reason = "platform";
                return null;
            }
            foreach (string candidate in ForceDimensionCatalog.Candidates(picked))
            {
                if (reason == "missing") reason = "load";
                if (!NativeLibrary.TryLoad(candidate, out IntPtr module)) continue;
                var lib = new ForceDimensionLibrary { Path = candidate };
                if (lib.Resolve(module)) return lib;
                // Loaded, and not the library the header describes. The next
                // candidate may be. The module stays mapped.
                reason = "exports";
            }
            return null;
        }

        private bool Resolve(IntPtr module)
            => Bind(module, "dhdGetDeviceCount", out _getDeviceCount)
               && Bind(module, "dhdOpenID", out _openId)
               && Bind(module, "dhdClose", out _close)
               && Bind(module, "dhdGetSystemType", out _getSystemType)
               && Bind(module, "dhdGetSerialNumber", out _getSerialNumber)
               && Bind(module, "dhdGetPosition", out _getPosition)
               && Bind(module, "dhdGetButtonMask", out _getButtonMask)
               && Bind(module, "dhdEnableForce", out _enableForce)
               && Bind(module, "dhdSetForce", out _setForce)
               && Bind(module, "dhdSetMaxForce", out _setMaxForce)
               && Bind(module, "dhdSetGravityCompensation", out _setGravityCompensation)
               && Bind(module, "dhdSetStandardGravity", out _setStandardGravity)
               && Bind(module, "dhdGetStatus", out _getStatus)
               && Bind(module, "dhdGetComFreq", out _getComFreq)
               && Bind(module, "dhdErrorGetLast", out _errorGetLast)
               && Bind(module, "dhdErrorGetLastStr", out _errorGetLastStr)
               && Bind(module, "dhdGetSDKVersionStr", out _getSdkVersionStr);

        private static bool Bind<T>(IntPtr module, string name, out T fn) where T : Delegate
        {
            fn = null;
            if (!NativeLibrary.TryGetExport(module, name, out IntPtr p) || p == IntPtr.Zero) return false;
            fn = Marshal.GetDelegateForFunctionPointer<T>(p);
            return true;
        }

        private static sbyte Id(int id) => (sbyte)Math.Clamp(id, sbyte.MinValue, sbyte.MaxValue);

        /// <summary>The SDK's version, as it gives it.</summary>
        public string SdkVersion
        {
            get
            {
                try { return Marshal.PtrToStringAnsi(_getSdkVersionStr()) ?? string.Empty; }
                catch (Exception) { return string.Empty; }
            }
        }

        public int DeviceCount()
        {
            try { return _getDeviceCount(); }
            catch (Exception) { return -1; }
        }

        public int OpenIndex(int index)
        {
            if (index < 0 || index > sbyte.MaxValue) return -1;
            try { return _openId((sbyte)index); }
            catch (Exception) { return -1; }
        }

        public int Close(int id)
        {
            try { return _close(Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SystemType(int id)
        {
            try { return _getSystemType(Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SerialNumber(int id, out ushort serial)
        {
            serial = 0;
            try { return _getSerialNumber(out serial, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int GetPosition(int id, out double x, out double y, out double z)
        {
            x = y = z = 0;
            try { return _getPosition(out x, out y, out z, Id(id)); }
            catch (Exception) { return -1; }
        }

        public uint ButtonMask(int id)
        {
            try { return _getButtonMask(Id(id)); }
            catch (Exception) { return 0; }
        }

        public int EnableForce(int id, bool on)
        {
            try { return _enableForce(on ? (byte)1 : (byte)0, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SetForce(int id, double x, double y, double z)
        {
            try { return _setForce(x, y, z, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SetMaxForce(int id, double newtons)
        {
            try { return _setMaxForce(newtons, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SetGravityCompensation(int id, bool on)
        {
            try { return _setGravityCompensation(on ? 1 : 0, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int SetStandardGravity(int id, double metersPerSecondSquared)
        {
            try { return _setStandardGravity(metersPerSecondSquared, Id(id)); }
            catch (Exception) { return -1; }
        }

        public int GetStatus(int id, int[] status)
        {
            if (status == null || status.Length < StatusCount) return -1;
            try { return _getStatus(status, Id(id)); }
            catch (Exception) { return -1; }
        }

        public double ComFrequency(int id)
        {
            try { return _getComFreq(Id(id)); }
            catch (Exception) { return 0; }
        }

        public int LastError()
        {
            try { return _errorGetLast(); }
            catch (Exception) { return -1; }
        }

        public string LastErrorText()
        {
            try { return Marshal.PtrToStringAnsi(_errorGetLastStr()) ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }
    }
}
