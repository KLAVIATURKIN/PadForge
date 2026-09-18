using System;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Common.OpenXr
{
    /// <summary>
    /// The narrow slice of OpenXR this app needs, reached by talking to a
    /// runtime directly instead of through the Khronos loader (issue #403).
    ///
    /// <para>A runtime DLL exports one entry point,
    /// <c>xrNegotiateLoaderRuntimeInterface</c>, which hands back
    /// <c>xrGetInstanceProcAddr</c>. Everything else is a function pointer
    /// obtained from that (OpenXR-SDK
    /// include/openxr/openxr_loader_negotiation.h). Doing the handshake here
    /// costs about a hundred lines and buys two things the loader cannot
    /// give this app.</para>
    ///
    /// <para>First, runtime selection. PadForge runs elevated, and the
    /// Khronos loader deliberately ignores <c>XR_RUNTIME_JSON</c> from a
    /// high-integrity process (OpenXR-SDK src/common/platform_utils.hpp,
    /// PlatformUtilsGetSecureEnv). Picking the manifest ourselves makes the
    /// choice per-process by construction, with nothing to configure
    /// globally and nothing to put back afterward.</para>
    ///
    /// <para>Second, isolation. The loader would insert any API layer the
    /// machine has installed into our process. A background client that only
    /// reads a head pose has no use for them, and a user's layers belong to
    /// their game.</para>
    /// </summary>
    internal static unsafe class OpenXrInterop
    {
        // ─── negotiation (openxr_loader_negotiation.h) ───

        public const int XR_LOADER_INTERFACE_STRUCT_LOADER_INFO = 1;
        public const int XR_LOADER_INTERFACE_STRUCT_RUNTIME_REQUEST = 3;
        public const uint XR_CURRENT_LOADER_RUNTIME_VERSION = 1;

        /// <summary>XR_MAKE_VERSION(major, minor, patch).</summary>
        public static ulong MakeVersion(ulong major, ulong minor, ulong patch)
            => ((major & 0xFFFF) << 48) | ((minor & 0xFFFF) << 32) | (patch & 0xFFFFFFFF);

        /// <summary>The API version this client speaks. 1.0 is asked for
        /// rather than 1.1 because every runtime that matters implements it
        /// and nothing here needs a 1.1 call.</summary>
        public static readonly ulong ApiVersion1_0 = MakeVersion(1, 0, 34);

        [StructLayout(LayoutKind.Sequential)]
        public struct XrNegotiateLoaderInfo
        {
            public int structType;
            public uint structVersion;
            public nuint structSize;
            public uint minInterfaceVersion;
            public uint maxInterfaceVersion;
            public ulong minApiVersion;
            public ulong maxApiVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrNegotiateRuntimeRequest
        {
            public int structType;
            public uint structVersion;
            public nuint structSize;
            public uint runtimeInterfaceVersion;
            public ulong runtimeApiVersion;
            public IntPtr getInstanceProcAddr;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrNegotiateLoaderRuntimeInterface(
            ref XrNegotiateLoaderInfo loaderInfo, ref XrNegotiateRuntimeRequest runtimeRequest);

        // ─── core enums and flags (openxr.h) ───

        public const int XR_SUCCESS = 0;
        public const int XR_SESSION_LOSS_PENDING = 3;
        public const int XR_EVENT_UNAVAILABLE = 4;

        public const int XR_TYPE_EXTENSION_PROPERTIES = 2;
        public const int XR_TYPE_INSTANCE_CREATE_INFO = 3;
        public const int XR_TYPE_SYSTEM_GET_INFO = 4;
        public const int XR_TYPE_SESSION_CREATE_INFO = 8;
        public const int XR_TYPE_SESSION_BEGIN_INFO = 10;
        public const int XR_TYPE_EVENT_DATA_BUFFER = 16;
        public const int XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING = 17;
        public const int XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED = 18;
        public const int XR_TYPE_INSTANCE_PROPERTIES = 32;
        public const int XR_TYPE_REFERENCE_SPACE_CREATE_INFO = 37;
        public const int XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING = 40;
        public const int XR_TYPE_SPACE_LOCATION = 42;

        public const int XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY = 1;
        public const int XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO = 2;

        public const int XR_REFERENCE_SPACE_TYPE_VIEW = 1;
        public const int XR_REFERENCE_SPACE_TYPE_LOCAL = 2;
        public const int XR_REFERENCE_SPACE_TYPE_STAGE = 3;

        public const int XR_SESSION_STATE_IDLE = 1;
        public const int XR_SESSION_STATE_READY = 2;
        public const int XR_SESSION_STATE_SYNCHRONIZED = 3;
        public const int XR_SESSION_STATE_VISIBLE = 4;
        public const int XR_SESSION_STATE_FOCUSED = 5;
        public const int XR_SESSION_STATE_STOPPING = 6;
        public const int XR_SESSION_STATE_LOSS_PENDING = 7;
        public const int XR_SESSION_STATE_EXITING = 8;

        public const ulong XR_SPACE_LOCATION_ORIENTATION_VALID_BIT = 0x1;
        public const ulong XR_SPACE_LOCATION_POSITION_VALID_BIT = 0x2;
        public const ulong XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT = 0x4;
        public const ulong XR_SPACE_LOCATION_POSITION_TRACKED_BIT = 0x8;

        public const int XR_MAX_EXTENSION_NAME_SIZE = 128;
        public const int XR_MAX_APPLICATION_NAME_SIZE = 128;
        public const int XR_MAX_ENGINE_NAME_SIZE = 128;
        public const int XR_MAX_RUNTIME_NAME_SIZE = 128;

        /// <summary>The headless extension, which is what lets a session
        /// exist with no graphics binding and no submitted frames
        /// (XR_MND_headless). Virtual Desktop's runtime advertises it, and so
        /// does SteamVR's.</summary>
        public const string XR_MND_HEADLESS_EXTENSION_NAME = "XR_MND_headless";

        // ─── core structs ───

        [StructLayout(LayoutKind.Sequential)]
        public struct XrVector3f
        {
            public float x, y, z;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrQuaternionf
        {
            public float x, y, z, w;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrPosef
        {
            public XrQuaternionf orientation;
            public XrVector3f position;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrApplicationInfo
        {
            public fixed byte applicationName[XR_MAX_APPLICATION_NAME_SIZE];
            public uint applicationVersion;
            public fixed byte engineName[XR_MAX_ENGINE_NAME_SIZE];
            public uint engineVersion;
            public ulong apiVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrInstanceCreateInfo
        {
            public int type;
            public IntPtr next;
            public ulong createFlags;
            public XrApplicationInfo applicationInfo;
            public uint enabledApiLayerCount;
            public IntPtr enabledApiLayerNames;
            public uint enabledExtensionCount;
            public IntPtr enabledExtensionNames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrInstanceProperties
        {
            public int type;
            public IntPtr next;
            public ulong runtimeVersion;
            public fixed byte runtimeName[XR_MAX_RUNTIME_NAME_SIZE];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrExtensionProperties
        {
            public int type;
            public IntPtr next;
            public fixed byte extensionName[XR_MAX_EXTENSION_NAME_SIZE];
            public uint extensionVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrSystemGetInfo
        {
            public int type;
            public IntPtr next;
            public int formFactor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrSessionCreateInfo
        {
            public int type;
            public IntPtr next;
            public ulong createFlags;
            public ulong systemId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrReferenceSpaceCreateInfo
        {
            public int type;
            public IntPtr next;
            public int referenceSpaceType;
            public XrPosef poseInReferenceSpace;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrSpaceLocation
        {
            public int type;
            public IntPtr next;
            public ulong locationFlags;
            public XrPosef pose;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrSessionBeginInfo
        {
            public int type;
            public IntPtr next;
            public int primaryViewConfigurationType;
        }

        /// <summary>The event union. Its payload is read by reinterpreting
        /// the buffer once <c>type</c> says which event arrived.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct XrEventDataBuffer
        {
            public int type;
            public IntPtr next;
            public fixed byte varying[4000];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XrEventDataSessionStateChanged
        {
            public int type;
            public IntPtr next;
            public ulong session;
            public int state;
            public long time;
        }

        // ─── function pointers ───

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrGetInstanceProcAddr(
            ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrEnumerateInstanceExtensionProperties(
            [MarshalAs(UnmanagedType.LPStr)] string layerName,
            uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrCreateInstance(ref XrInstanceCreateInfo createInfo, out ulong instance);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrDestroyInstance(ulong instance);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrGetInstanceProperties(ulong instance, IntPtr properties);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrGetSystem(ulong instance, ref XrSystemGetInfo getInfo, out ulong systemId);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrCreateSession(ulong instance, ref XrSessionCreateInfo createInfo, out ulong session);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrDestroySession(ulong session);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrBeginSession(ulong session, ref XrSessionBeginInfo beginInfo);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrEndSession(ulong session);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrCreateReferenceSpace(
            ulong session, ref XrReferenceSpaceCreateInfo createInfo, out ulong space);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrDestroySpace(ulong space);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrLocateSpace(ulong space, ulong baseSpace, long time, ref XrSpaceLocation location);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PFN_xrPollEvent(ulong instance, IntPtr eventData);

        /// <summary>A fixed-size UTF-8 name field as a string.</summary>
        public static string ReadFixedUtf8(byte* start, int capacity)
        {
            int n = 0;
            while (n < capacity && start[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(start, n);
        }

        /// <summary>Copies a name into a fixed-size UTF-8 field, always null
        /// terminated. A name too long for the field is truncated rather than
        /// overrunning it.</summary>
        public static void WriteFixedUtf8(byte* dest, int capacity, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            int n = Math.Min(bytes.Length, capacity - 1);
            for (int i = 0; i < n; i++) dest[i] = bytes[i];
            for (int i = n; i < capacity; i++) dest[i] = 0;
        }
    }
}
