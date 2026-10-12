using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test host is kept off the mutex third-party Razer tools share:
    /// the guard refuses that name here, it asks before it makes or opens
    /// anything, and nothing in the app turns the refusal on. No test in
    /// this file makes, opens or takes the shared mutex itself. Its name is
    /// pinned by reading the source.
    /// </summary>
    public class RazerMutexOffLimitsTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string GuardSource()
            => File.ReadAllText(Path.Combine(Root(), "PadForge.App", "Common", "Input", "Peripherals", "RazerGuard.cs"));

        /// <summary>The refusal is on in the test host, and it covers the
        /// shared name in every namespace and in any case. It is shown here
        /// on that name in the session's own namespace, the nearest thing
        /// to it that is not the mutex tools share: the guard makes no
        /// mutex, every take answers at once as it does where the mutex
        /// cannot be had, and the log says so once.</summary>
        [Fact]
        public void TheTestHostRefusesTheSharedMutex()
        {
            Assert.True(RazerGuard.SharedMutexOffLimits);
            Assert.True(RazerGuard.IsShared(RazerGuard.Name));
            Assert.True(RazerGuard.IsShared(@"Global\razerlinkreadwriteguardmutex"));
            Assert.True(RazerGuard.IsShared("RazerLinkReadWriteGuardMutex"));

            const string line = "PERIPHERAL Razer lock not available (off limits in the test host)";
            foreach (string name in new[] { @"Local\RazerLinkReadWriteGuardMutex", @"Local\RAZERLINKREADWRITEGUARDMUTEX" })
            {
                Assert.True(RazerGuard.IsShared(name));
                string mark = DiagMark.Write();
                using var guard = new RazerGuard(name);
                Assert.False(guard.Unavailable);
                Assert.True(guard.Take(0));
                Assert.True(guard.Unavailable);
                Assert.True(guard.Take(RazerGuard.CommandWaitMs));
                guard.Release();
                guard.Release();
                Assert.Equal(1, DiagMark.LinesSince(mark, line));
            }

            // A mutex of a test's own, with a name or without one, is not
            // the shared one.
            Assert.False(RazerGuard.IsShared(null));
            Assert.False(RazerGuard.IsShared(""));
            Assert.False(RazerGuard.IsShared(@"Local\PadForge-test-razer-guard-held"));
            Assert.False(RazerGuard.IsShared(@"Global\RazerLinkReadWriteGuardMutex2"));
            Assert.False(RazerGuard.IsShared(@"Global\RazerLinkReadWriteGuardMutex\other"));
        }

        /// <summary>The shared name is OpenRGB's, the guard the app makes
        /// is on it, and the guard asks whether it is off limits before
        /// the one place that makes or opens a mutex. Read from the source,
        /// so nothing here reaches the mutex to find out.</summary>
        [Fact]
        public void TheGuardAsksBeforeItMakesOrOpensTheMutex()
        {
            string source = GuardSource();
            Assert.Contains("internal const string Name = @\"Global\\RazerLinkReadWriteGuardMutex\";", source);
            Assert.Matches(@"public RazerGuard\(\)\s*: this\(Name\) \{ \}", source);

            const string signature = "private Mutex Open()";
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            int asks = source.IndexOf("if (SharedMutexOffLimits && IsShared(_name))", at, StringComparison.Ordinal);
            Assert.True(asks > at, "Open never asks");
            // Every call that makes or opens a mutex comes after the ask,
            // and the ask's own branch leaves without one.
            Assert.Equal(-1, source.IndexOf("MutexAcl.", 0, asks, StringComparison.Ordinal));
            int first = source.IndexOf("MutexAcl.", asks, StringComparison.Ordinal);
            Assert.True(first > asks, "Open makes no mutex");
            string refusal = source.Substring(asks, first - asks);
            Assert.Contains("return _mutex;", refusal);
            Assert.DoesNotContain("new Mutex(", source);
            Assert.DoesNotContain("Mutex.OpenExisting", source);
            Assert.DoesNotContain("Mutex.TryOpenExisting", source);
        }

        [Fact]
        public void NothingInTheAppTurnsTheRefusalOn()
        {
            string root = Root();
            char slash = Path.DirectorySeparatorChar;
            var callers = new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.Contains(slash + "obj" + slash) && !path.Contains(slash + "bin" + slash))
                .Where(path => File.ReadAllText(path).Contains("KeepOffTheSharedMutexForTest", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Equal(new[] { "RazerGuard.cs" }, callers);

            // In that file it is defined, and named in comments, and never
            // called: the one opening parenthesis after its name is its own.
            string source = GuardSource();
            Assert.Single(Regex.Matches(source, @"KeepOffTheSharedMutexForTest\("));
            Assert.Contains("internal static void KeepOffTheSharedMutexForTest() => Volatile.Write(ref s_sharedOffLimits, 1);", source);
            Assert.Single(Regex.Matches(source, @"Volatile\.Write\(ref s_sharedOffLimits"));
        }
    }
}
