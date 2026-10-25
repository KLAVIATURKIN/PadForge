using System;
using System.Globalization;
using System.IO;

namespace PadForge.Services
{
    /// <summary>
    /// The files DSX game mods read their port from (#529).
    ///
    /// <para>Mods built from DSX's v2 example read
    /// <c>C:\Temp\DualSenseX\DualSenseX_PortNumber.txt</c>, and some stop when
    /// it is missing: DualSense4Rockstar's GTA IV client reads it with no
    /// fallback (DualSense4GTAIV/Main/iO.cs:37). Mods built from the v3.1
    /// example read <c>%LOCALAPPDATA%\DSX\DSX_UDP_PortNumber.txt</c> and fall
    /// back to 6969 (Program.cs, FetchPortNumber). DSX writes both. DSY's
    /// first version created the old file when it was missing
    /// (UDP.cs:24-28).</para>
    ///
    /// <para>When the server starts listening, the old file is created when
    /// missing, either file holding another port is rewritten, and the new
    /// file is created only for a port other than 6969, which its readers
    /// fall back to anyway. Both are left alone while a real DSX runs, since
    /// DSX owns them then.</para>
    /// </summary>
    internal static class GameModPortFiles
    {
        internal const string LegacyPath = @"C:\Temp\DualSenseX\DualSenseX_PortNumber.txt";

        internal static string CurrentPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSX", "DSX_UDP_PortNumber.txt");

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests GameModPortFilesOffLimits), never by the app. The
        /// files are the PC's own, read by every DSX mod on it, so the tests
        /// write files of their own through <see cref="WriteTo"/>. False in
        /// production.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Writes the port where the mods look, unless a real DSX
        /// is running. Never throws.</summary>
        internal static void Write(int port)
        {
            if (OffLimitsForTest) return;
            try
            {
                if (RealDsxRunning()) return;
                WriteTo(LegacyPath, CurrentPath, port);
            }
            catch
            {
                // A file the mods cannot read is the mods' fallback, never a
                // reason to stop the server.
            }
        }

        /// <summary>The rule, on any two paths.</summary>
        internal static void WriteTo(string legacyPath, string currentPath, int port)
        {
            WriteOne(legacyPath, port, createWhenMissing: true);
            WriteOne(currentPath, port, createWhenMissing: port != GameModServer.DefaultPort);
        }

        private static void WriteOne(string path, int port, bool createWhenMissing)
        {
            try
            {
                if (File.Exists(path))
                {
                    string held = File.ReadAllText(path).Trim();
                    if (int.TryParse(held, NumberStyles.Integer, CultureInfo.InvariantCulture, out int heldPort)
                        && heldPort == port)
                        return;
                }
                else if (!createWhenMissing)
                {
                    return;
                }
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(path, port.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Whether a process named DSX runs that is not PadForge's
        /// own stand-in, read from the one process list the app walks
        /// (RunningProcessList, ProcessPresenceTests).</summary>
        private static bool RealDsxRunning()
        {
            var running = Common.Input.RunningProcessList.Read();
            if (running == null) return false;
            uint standIn = (uint)DsxStandIn.ProcessId;
            foreach (var p in running)
                if (p.Id != standIn && string.Equals(p.Name, "DSX", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
