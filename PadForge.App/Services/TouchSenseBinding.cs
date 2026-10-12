using System;
using System.Collections.Generic;
using Nefarius.Utilities.DeviceManagement.PnP;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Services
{
    /// <summary>
    /// What Windows says about a TouchSense mouse's driver (#494), read and
    /// never changed here: which of the mice are bound to PadForge's WinUSB
    /// package now, and whether a mouse still on Windows' HID driver
    /// declares the buzz report, which decides whether it is offered the
    /// binding at all. The binding itself and its restore are
    /// <see cref="VendorUsbDriverInstaller"/>'s, made only from the Devices
    /// page after its confirm. The test host reads neither from the PC it
    /// runs on (<see cref="RealTouchSenseAccess"/>,
    /// <see cref="RealHidAccess"/>): a test hands in the paths and the
    /// collections it means.
    /// </summary>
    internal static class TouchSenseBinding
    {
        private const int HidP_Output = 1;

        /// <summary>The bound mice that are plugged in, or null when the
        /// look itself failed. A bound mouse is no HID collection and no
        /// Raw Input device, so this is the one way to find it: the
        /// interfaces PadForge's package registers on the nodes it binds
        /// (<see cref="VendorUsbDriverInstaller.InterfaceGuid"/>).</summary>
        internal static List<TouchSenseMouse> FindBound()
        {
            // The test host looks at no real PC (RealTouchSenseAccess). A
            // look that is refused reads as one that failed.
            if (RealTouchSenseAccess.Refused) return null;
            var paths = VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid);
            return paths == null ? null : BoundMice(paths);
        }

        /// <summary>The TouchSense mice among the interface paths of
        /// PadForge's packages, each with the path as Windows gave it.</summary>
        internal static List<TouchSenseMouse> BoundMice(IEnumerable<string> interfacePaths)
        {
            var mice = new List<TouchSenseMouse>();
            if (interfacePaths == null) return mice;
            foreach (string path in interfacePaths)
                if (TryParseBoundPath(path, out ushort vendorId, out ushort productId)
                    && TouchSenseProtocol.IsMouse(vendorId, productId))
                    mice.Add(new TouchSenseMouse(path, vendorId, productId));
            return mice;
        }

        /// <summary>The IDs in a WinUSB interface path of a whole device,
        /// <c>\\?\usb#vid_046d&amp;pid_c030#6&amp;1c2d3e4f&amp;0&amp;2#{...}</c>:
        /// the instance ID with # for each backslash, then the interface's
        /// GUID. False for the interface child of a composite device, which
        /// is never what a TouchSense mouse's package binds.</summary>
        internal static bool TryParseBoundPath(string path, out ushort vendorId, out ushort productId)
        {
            vendorId = productId = 0;
            const string prefix = @"\\?\";
            if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = path.Substring(prefix.Length).Split('#');
            if (parts.Length < 3) return false;
            if (VendorUsbDriverInstaller.TryParseUsbInstanceId(parts[0] + "\\" + parts[1] + "\\" + parts[2],
                    out vendorId, out productId, out int mi)
                && mi < 0)
                return true;
            vendorId = productId = 0;
            return false;
        }

        /// <summary>
        /// Whether the mouse collection of a USB node on Windows' HID
        /// driver declares the buzz report
        /// (<see cref="TouchSenseProtocol.MouseCollectionDeclaresBuzz"/>),
        /// read from this PC: the present collections, each one's parent
        /// node, and what hid.dll already parsed of its output report,
        /// through an open with no access right, which Windows grants on a
        /// mouse collection. Nothing is sent to the device. False on any
        /// failure, and in the test host, which reads no real collection
        /// (<see cref="RealHidAccess"/>): a mouse that cannot be read is
        /// offered nothing.
        /// </summary>
        internal static bool NodeDeclaresBuzz(VendorUsbDriverInstaller.UsbNode node)
        {
            if (RealHidAccess.Refused) return false;
            try
            {
                return NodeDeclaresBuzz(node, HidCollectionRuntime.Enumerate(), ParentOf, ReadOutputCaps);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Reads a collection's output capabilities: its output
        /// button count and each output value. False when it cannot be
        /// read.</summary>
        internal delegate bool OutputCapsReader(string path, out int buttons, out List<TouchSenseValueCap> values);

        /// <summary>
        /// The same answer from parts a caller hands in: the collections,
        /// the lookup of a collection's parent node, and the reader of its
        /// output capabilities. Each mouse collection with the node's IDs
        /// is tied to the node through its parent, so a second mouse of
        /// the same model on another port answers for itself.
        /// </summary>
        internal static bool NodeDeclaresBuzz(VendorUsbDriverInstaller.UsbNode node,
            IReadOnlyList<HidCollectionInfo> collections, Func<string, string> parentOf, OutputCapsReader readCaps)
        {
            if (collections == null || parentOf == null || readCaps == null || string.IsNullOrEmpty(node.InstanceId))
                return false;
            foreach (var collection in collections)
            {
                if (collection == null || collection.VendorId != node.Vid || collection.ProductId != node.Pid
                    || collection.UsagePage != 0x0001 || collection.Usage != 0x0002)
                    continue;
                try
                {
                    if (!string.Equals(parentOf(collection.Path), node.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (readCaps(collection.Path, out int buttons, out var values)
                        && TouchSenseProtocol.MouseCollectionDeclaresBuzz(collection.UsagePage, collection.Usage,
                            collection.OutputReportLength, collection.FeatureReportLength, buttons, values))
                        return true;
                }
                catch
                {
                    // A collection that cannot be read declares nothing.
                }
            }
            return false;
        }

        /// <summary>The instance ID of the parent of the device behind a
        /// HID interface path, or null. For the one collection of a mouse
        /// with one interface that parent is the USB device's own node,
        /// the node a TouchSense mouse's package binds.</summary>
        internal static string ParentOf(string hidPath)
        {
            try
            {
                string instance = PnPDevice.GetInstanceIdFromInterfaceId(hidPath);
                if (string.IsNullOrEmpty(instance)) return null;
                return PnPDevice.GetDeviceByInstanceId(instance, DeviceLocationFlags.Normal)
                    .GetProperty<string>(DevicePropertyKey.Device_Parent);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A collection's output capabilities as hid.dll parsed
        /// them, through a query-only open. The test host opens no
        /// collection (<see cref="RealHidAccess"/>).</summary>
        internal static bool ReadOutputCaps(string path, out int buttons, out List<TouchSenseValueCap> values)
        {
            buttons = 0;
            values = new List<TouchSenseValueCap>();
            if (RealHidAccess.Refused) return false;
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return false;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS) return false;
                buttons = caps.NumberOutputButtonCaps;
                foreach (var value in SonyHeadsetHid.GetValueCaps(HidP_Output, preparsed, caps.NumberOutputValueCaps))
                {
                    // A range of usages is never the one usage the buzz
                    // report has, so it is listed as none.
                    values.Add(new TouchSenseValueCap(value.ReportID, value.UsagePage,
                        value.IsRange ? (ushort)0 : value.UsageMin, value.BitSize, value.ReportCount));
                }
                return values.Count == caps.NumberOutputValueCaps;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                handle.Dispose();
            }
        }
    }
}
