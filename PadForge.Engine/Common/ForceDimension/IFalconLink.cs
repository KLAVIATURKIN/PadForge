namespace PadForge.Engine.Common.ForceDimension
{
    /// <summary>
    /// The calls PadForge makes into Force Dimension's library to drive a
    /// Novint Falcon (issue #521), as <c>dhdc.h</c> declares them. The app
    /// talks to <see cref="ForceDimensionLibrary"/> through this, and tests
    /// talk to a scripted Falcon.
    ///
    /// <para>Every call but the first takes the device ID that
    /// <see cref="OpenIndex"/> returned. The library is thread-safe, and its
    /// last error is kept per thread (the SDK reference's "Thread-Safe
    /// Operation" and <c>dhdErrorGetLast</c>), so each Falcon's haptic
    /// thread makes its own calls.</para>
    /// </summary>
    public interface IFalconLink
    {
        /// <summary>The devices Force Dimension's library sees, those other
        /// programs hold included, or -1.</summary>
        int DeviceCount();

        /// <summary>Opens the device at an enumeration index and returns its
        /// ID, -1 on failure. A device already open returns its ID again.</summary>
        int OpenIndex(int index);

        int Close(int id);

        /// <summary>60 for a Falcon (<c>DHD_DEVICE_FALCON</c>).</summary>
        int SystemType(int id);

        /// <summary>0 with the serial number, -1 when the device has none
        /// to give.</summary>
        int SerialNumber(int id, out ushort serial);

        /// <summary>The handle's position in meters, 0 or 1 on success.</summary>
        int GetPosition(int id, out double x, out double y, out double z);

        /// <summary>Bit n set while button n is down.</summary>
        uint ButtonMask(int id);

        int EnableForce(int id, bool on);

        /// <summary>The force on the handle in newtons, 0 or 2 on success.</summary>
        int SetForce(int id, double x, double y, double z);

        int SetMaxForce(int id, double newtons);

        int SetGravityCompensation(int id, bool on);

        int SetStandardGravity(int id, double metersPerSecondSquared);

        /// <summary>Fills a 17-entry status vector.</summary>
        int GetStatus(int id, int[] status);

        /// <summary>The rate force calls reached since the last call, in kHz.</summary>
        double ComFrequency(int id);

        /// <summary>The calling thread's last error code.</summary>
        int LastError();

        /// <summary>The calling thread's last error, in Force Dimension's words.</summary>
        string LastErrorText();
    }
}
