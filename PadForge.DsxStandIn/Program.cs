// DSX.exe, PadForge's stand-in for DSX game mods (#529).
//
// Some mods made for DSX look for a running process named DSX and turn
// themselves off without one: DualSense4GTAV checks
// Process.GetProcessesByName("DSX") on every lights tick (iO.cs:61). While
// PadForge's game mod server listens with Run a DSX.exe Stand-In on, PadForge
// starts this program under that name. It waits for the PadForge that started
// it to exit and then exits too. PadForge also ends it when the server stops,
// and a job object ends it if PadForge itself ends first.
using System;
using System.ComponentModel;
using System.Diagnostics;

internal static class Program
{
    private static int Main(string[] args)
    {
        int parent;
        if (args.Length < 1 || !int.TryParse(args[0], out parent)) return 1;
        try
        {
            using (var process = Process.GetProcessById(parent))
                process.WaitForExit();
        }
        catch (ArgumentException) { }          // PadForge is already gone.
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        return 0;
    }
}
