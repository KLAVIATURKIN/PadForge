using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Vosk recognition model ships INSIDE the executable (#317). It used
    /// to download ~40 MB on first use, which made a feature advertised as
    /// offline unusable on a machine with no internet.
    ///
    /// <para>The failure mode this pins is silent by construction: if the
    /// resource name drifts, <c>GetManifestResourceStream</c> returns null,
    /// the unpack throws, and voice macros fall back to the Windows speech
    /// engine forever. Nothing crashes and nothing logs at a level anyone
    /// reads, so the feature just quietly gets worse. A renamed folder, a
    /// bumped model version, or a dropped csproj glob all land here.</para>
    /// </summary>
    public class EmbeddedVoiceModelTests
    {
        // The exact string VoskModelStore asks for.
        private const string ModelResource =
            "PadForge.VoiceModels.vosk-model-small-en-us-0.15.zipbr";

        private static Assembly AppAssembly =>
            Assembly.Load("PadForge");

        /// <summary>The model is embedded packed: an archive whose members
        /// are stored rather than deflated, compressed as a whole. That is
        /// 4 MB smaller than the archive compressing its own members, because
        /// compressing each member separately shows the compressor 15 MB of
        /// acoustic weights at a time instead of all 68 MB at once. Unpacking
        /// gives the archive back.</summary>
        private static MemoryStream Unpack()
        {
            using var packed = AppAssembly.GetManifestResourceStream(ModelResource);
            Assert.NotNull(packed);
            var archive = new MemoryStream();
            using (var brotli = new BrotliStream(packed, CompressionMode.Decompress))
                brotli.CopyTo(archive);
            archive.Position = 0;
            return archive;
        }

        [Fact]
        public void TheModelIsEmbeddedUnderTheNameTheLoaderAsksFor()
        {
            var names = AppAssembly.GetManifestResourceNames();
            Assert.True(names.Contains(ModelResource),
                "embedded model missing. Present resources matching 'vosk': "
                + string.Join(", ", names.Where(n => n.Contains("vosk", System.StringComparison.OrdinalIgnoreCase)))
                + " (none, if blank)");
        }

        /// <summary>A resource of the right NAME that is not a readable
        /// archive fails just as silently, so open it.</summary>
        [Fact]
        public void TheEmbeddedModelIsAReadableArchiveCarryingTheModel()
        {
            using var s = Unpack();
            using var zip = new ZipArchive(s, ZipArchiveMode.Read);

            // Vosk loads a model from a directory, and the archive carries a
            // single top-level folder the unpack promotes. The acoustic model
            // is the file whose absence makes Model() throw.
            Assert.Contains(zip.Entries, e =>
                e.FullName.EndsWith("am/final.mdl", System.StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("final.mdl", System.StringComparison.OrdinalIgnoreCase));

            var top = zip.Entries
                .Select(e => e.FullName.Split('/')[0])
                .Distinct()
                .ToArray();
            Assert.Single(top);
        }

        /// <summary>Every member survives the repack, byte for byte.
        ///
        /// <para>Packing rewrites the archive so its members are stored, and
        /// a rewrite can drop or truncate one while the result still opens as
        /// a zip and still carries a plausible top-level folder. The failure
        /// would surface much later, as a speech engine refusing a model
        /// directory, on the first machine that ever turned voice macros
        /// on.</para></summary>
        [Fact]
        public void EveryMemberSurvivesThePack()
        {
            string source = Path.Combine(RepoRoot(), "PadForge.App", "VoiceModels",
                                         "vosk-model-small-en-us-0.15.zip");
            Assert.True(File.Exists(source), source);

            using var unpacked = Unpack();
            using var packed = new ZipArchive(unpacked, ZipArchiveMode.Read);
            using var original = ZipFile.OpenRead(source);

            Assert.Equal(
                original.Entries.Select(e => e.FullName).OrderBy(n => n, System.StringComparer.Ordinal),
                packed.Entries.Select(e => e.FullName).OrderBy(n => n, System.StringComparer.Ordinal));

            foreach (var entry in original.Entries)
            {
                var mine = packed.GetEntry(entry.FullName);
                Assert.True(mine != null, $"{entry.FullName} is missing from the packed model");
                Assert.True(mine.Length == entry.Length,
                    $"{entry.FullName} is {mine.Length} bytes packed but {entry.Length} at source");
                Assert.Equal(Hash(entry), Hash(mine));
            }
        }

        /// <summary>The unpack the engine performs actually works.
        ///
        /// <para>The test above reads the archive out of memory. The engine
        /// stages it to a file first, because it expands to 68 MB and
        /// ZipArchive seeks around it, and then extracts. This walks that
        /// same path on one member so a mistake in it fails here rather than
        /// on a user's first voice macro.</para></summary>
        [Fact]
        public void TheModelStagesToAFileAndExtracts()
        {
            string staged = Path.Combine(Path.GetTempPath(),
                                         "PadForge-voice-model-test-" + Path.GetRandomFileName());
            string into = staged + ".out";
            try
            {
                using (var packed = AppAssembly.GetManifestResourceStream(ModelResource))
                using (var brotli = new BrotliStream(packed, CompressionMode.Decompress))
                using (var file = File.Create(staged))
                    brotli.CopyTo(file);

                using var zip = ZipFile.OpenRead(staged);
                var acoustic = zip.Entries.First(e =>
                    e.FullName.EndsWith("am/final.mdl", System.StringComparison.OrdinalIgnoreCase));

                Directory.CreateDirectory(into);
                string target = Path.Combine(into, "final.mdl");
                acoustic.ExtractToFile(target);
                Assert.True(new FileInfo(target).Length == acoustic.Length,
                    "the acoustic model did not extract to its full length");
            }
            finally
            {
                try { File.Delete(staged); } catch { }
                try { Directory.Delete(into, true); } catch { }
            }
        }

        private static string Hash(ZipArchiveEntry entry)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return System.Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
        }

        /// <summary>No network path may return. The whole point is that a
        /// machine which has never been online can still recognize speech.</summary>
        [Fact]
        public void TheLoaderCarriesNoDownloadUrl()
        {
            string src = Path.Combine(RepoRoot(), "PadForge.App", "Services", "VoskVoiceEngine.cs");
            Assert.True(File.Exists(src), src);
            string text = File.ReadAllText(src);
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
            Assert.DoesNotContain("HttpClient", text);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            return d?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
