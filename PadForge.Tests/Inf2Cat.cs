using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Runs Inf2Cat, from the toolchain the signing step extracts, on one INF
    /// with the /os value <see cref="Ds3DriverInstaller.CatalogOs"/> gives each
    /// architecture. Inf2Cat checks the INF and writes the catalog. Nothing is
    /// signed and nothing is installed.
    /// </summary>
    internal static class Inf2Cat
    {
        /// <summary>The Inf2Cat tree, unpacked from HIDMaestro.Core's own
        /// resources into <paramref name="tools"/>: the files
        /// DriverBuilder.PayloadFor names under HIDMaestro.Resources, the
        /// architecture-neutral half of the payload, which is Inf2Cat and the
        /// libraries it loads. The signing step reaches the same files through
        /// DriverBuilder.EnsureExtracted, which from HIDMaestro 2.0.0 stages
        /// them only under %ProgramFiles%\HIDMaestro and only for an elevated
        /// process. The test host is not elevated, and Inf2Cat needs no
        /// elevation.</summary>
        private static string Unpack(string tools)
        {
            var sdk = typeof(HIDMaestro.HMContext).Assembly;
            var payloadFor = typeof(HIDMaestro.Internal.DriverBuilder).GetMethod("PayloadFor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(payloadFor);
            Directory.CreateDirectory(tools);
            int unpacked = 0;
            foreach (var (file, logical) in (System.Collections.Generic.IEnumerable<(string File, string Logical)>)
                     payloadFor.Invoke(null, new object[] { RuntimeInformation.OSArchitecture }))
            {
                if (!logical.StartsWith("HIDMaestro.Resources.", StringComparison.Ordinal)) continue;
                using var source = sdk.GetManifestResourceStream(logical);
                Assert.True(source != null, "HIDMaestro.Core carries no " + logical);
                using var target = File.Create(Path.Combine(tools, file));
                source.CopyTo(target);
                unpacked++;
            }
            // Positive control: the neutral half was found and holds Inf2Cat.
            Assert.True(unpacked > 0, "HIDMaestro.Core names no Inf2Cat payload");
            return Path.Combine(tools, "Inf2Cat.exe");
        }

        public static void AssertCatalogs(string infName, string infText, string catalogName, ITestOutputHelper output)
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests", "Inf2Cat", Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, "package");
            try
            {
                string inf2cat = Unpack(Path.Combine(root, "tools"));
                Assert.True(File.Exists(inf2cat), inf2cat);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, infName), infText, Encoding.ASCII);
                foreach (var machine in new[] { Architecture.X64, Architecture.Arm64 })
                {
                    string os = Ds3DriverInstaller.CatalogOs(machine);
                    foreach (string cat in Directory.GetFiles(dir, "*.cat")) File.Delete(cat);
                    var psi = new System.Diagnostics.ProcessStartInfo(inf2cat, $"/driver:\"{dir}\" /os:{os}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    var text = new StringBuilder();
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (text) text.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    Assert.True(p.WaitForExit(120000), "Inf2Cat timed out");
                    p.WaitForExit();
                    string log;
                    lock (text) log = text.ToString();
                    output.WriteLine($"{machine} /os:{os} exit {p.ExitCode}\n{log}");
                    Assert.True(p.ExitCode == 0, $"{os}: Inf2Cat exited {p.ExitCode}: {log}");
                    Assert.True(File.Exists(Path.Combine(dir, catalogName)), $"{os}: no {catalogName}");
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }
}
