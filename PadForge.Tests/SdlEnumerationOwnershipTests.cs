using System.Diagnostics;
using System.IO;
using Xunit.Abstractions;

namespace PadForge.Tests;

public sealed class SdlEnumerationOwnershipTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("SDL_GetJoysticks")]
    [InlineData("SDL_GetKeyboards")]
    [InlineData("SDL_GetMice")]
    public async Task EmptyNativeEnumerationReleasesItsAllocatedArray(string symbol)
    {
        var (code, log) = await NativeCheckRunner.Run(symbol);
        output.WriteLine(log);
        Assert.Equal(0, code);
        Assert.Contains("nativeControl=0 wrapperCalls=5 allocations=6 outstanding=0", log);
    }
}

internal static class NativeCheckRunner
{
    internal static async Task<(int Code, string Log)> Run(string symbol)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PadForge.sln"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string check = Path.Combine(root.FullName, "PadForge.NativeChecks", "bin", configuration, "net10.0-windows", "PadForge.NativeChecks.dll");
        string library = Path.Combine(root.FullName, "PadForge.App", "Resources", "SDL3", "x64", "SDL3.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root.FullName
        };
        start.ArgumentList.Add(check);
        start.ArgumentList.Add(library);
        start.ArgumentList.Add(symbol);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        string log = await stdout;
        string errors = await stderr;
        return (process.ExitCode, log + errors);
    }
}
