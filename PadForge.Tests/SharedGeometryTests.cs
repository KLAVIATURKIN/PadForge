using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A colorway that shares another's meshes renders the same shell.
    ///
    /// <para>Most colorways are paint on the same molded shell, and each one
    /// used to ship its own copy of the same 32 meshes: 341 files and 83.6 MB
    /// of byte-identical text. The copies are gone and the loader falls back
    /// to the colorway that kept them.</para>
    ///
    /// <para>A fallback that misses is silent. Meshes load through
    /// TryLoadModel, and a part that fails to resolve comes back null and is
    /// simply absent from the pad, so a lost stick ring looks like a modeling
    /// choice rather than a missing file. These tests compare a sharing
    /// colorway against the one it shares from, part for part and triangle
    /// for triangle.</para>
    /// </summary>
    public class SharedGeometryTests
    {
        public static TheoryData<string, string> SharedPairs()
        {
            var data = new TheoryData<string, string>();
            foreach (var pair in Table())
                data.Add(pair.Key, pair.Value);
            return data;
        }

        private static Dictionary<string, string> Table()
            => (Dictionary<string, string>)typeof(ControllerModelBase)
                .GetField("SharedGeometry", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

        [Theory]
        [MemberData(nameof(SharedPairs))]
        public void ASharingColorwayMatchesTheOneItSharesFrom(string colorway, string donor)
        {
            var (family, appearance) = Split(colorway);
            var (_, donorAppearance) = Split(donor);

            using var a = ControllerModelBase.Create(family, appearance, true);
            using var b = ControllerModelBase.Create(family, donorAppearance, true);

            var left = Inventory(a);
            var right = Inventory(b);

            Assert.Equal(right.Keys.OrderBy(k => k), left.Keys.OrderBy(k => k));

            // A colorway that kept no mesh of its own is rendering the
            // donor's shell outright, so every part must match triangle for
            // triangle. That is what catches an entry pointing at the wrong
            // donor, which would otherwise render a plausible but different
            // pad. Three colorways keep some meshes of their own and legibly
            // differ, so for those the shared part names are the claim.
            if (OwnMeshCount(colorway) > 0) return;

            foreach (var part in right.Keys)
                Assert.True(left[part] == right[part],
                    $"{colorway}: {part} has {left[part]} triangles, but {donor} has "
                    + $"{right[part]}, so the fallback resolved a different mesh");
        }

        private static int OwnMeshCount(string modelName)
            => typeof(ControllerModelBase).Assembly.GetManifestResourceNames()
                .Count(n => n.Contains($".{modelName}.", StringComparison.OrdinalIgnoreCase)
                            && n.EndsWith(MeshExtension, StringComparison.OrdinalIgnoreCase));

        /// <summary>A fallback resolves in one hop.
        ///
        /// <para>Nothing in the loader follows a chain, so an entry pointing
        /// at a colorway that itself shares would leave the meshes
        /// unreachable and the parts silently absent.</para></summary>
        [Theory]
        [MemberData(nameof(SharedPairs))]
        public void AFallbackTargetKeepsItsOwnMeshes(string colorway, string donor)
        {
            Assert.False(Table().ContainsKey(donor),
                $"{colorway} falls back to {donor}, which itself falls back, so the "
                + "lookup would need a second hop it does not make");
            Assert.True(OwnMeshCount(donor) > 0,
                $"{donor} keeps no meshes, so nothing can fall back to it");
        }

        /// <summary>Every colorway still builds with a body.
        ///
        /// <para>MainBody is the one mandatory mesh, and its loader throws
        /// when it cannot be found, so this covers the colorways that share
        /// nothing as well as the ones that do.</para></summary>
        [Theory]
        [MemberData(nameof(ControllerAtlasFormatTests.EveryAppearance),
                    MemberType = typeof(ControllerAtlasFormatTests))]
        public void EveryAppearanceStillHasABody(string family, string appearance)
        {
            using var model = ControllerModelBase.Create(family, appearance, true);
            Assert.NotNull(model.MainBody);
            Assert.True(Triangles(model.MainBody) > 0,
                $"{family}/{appearance ?? "default"}: the body carries no triangles");
        }

        /// <summary>The body each sharing colorway renders, recorded from the
        /// tree before its copy was dropped.
        ///
        /// <para>This is the anchor the rest of the file needs. Comparing a
        /// colorway against the donor the table names proves nothing about
        /// the table, because whatever donor it names is the one the colorway
        /// then loads: point Robot at Electric Volt and the comparison still
        /// succeeds while the pad renders the wrong shell. These hashes come
        /// from outside the table, so a wrong entry has something to
        /// contradict.</para>
        ///
        /// <para>MainBody is the mesh worth anchoring. It is the largest and
        /// the most distinctive, no two shells in a family share one, and
        /// every colorway has it.</para></summary>
        private static readonly Dictionary<string, string> ExpectedBody =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["DS4.MagmaRed"] = "3A26D4610D008ED547B463F96B816091DD4904D9EA79DA3F7DF36EC4F9994437",
                ["DualSense.GrayCamo"] = "1702DA6BB482851B34006DD55BB6F312955056C37370AB66C19103E7B964B176",
                ["DualSense.NovaPink"] = "1702DA6BB482851B34006DD55BB6F312955056C37370AB66C19103E7B964B176",
                ["DualSense.DeepEarthSterling"] = "5B2CE41545FC36FA9AA9012AE7C25B9FA987005BF0BE50FCED7BD50C0481D6E5",
                ["DualSense.DeepEarthVolcanic"] = "5B2CE41545FC36FA9AA9012AE7C25B9FA987005BF0BE50FCED7BD50C0481D6E5",
                ["XboxSeries.Robot"] = "20E39C916AEAB4C8774F37156F0096F0DC1170DA0679A5A41B81E2C229105C82",
                ["XboxSeries.PulseRed"] = "20E39C916AEAB4C8774F37156F0096F0DC1170DA0679A5A41B81E2C229105C82",
                ["XboxSeries.DeepPink"] = "AB0CAF7858AB3A95C04384BC2239B37BE493E91FF266E0A7EA97F523AA7B9D3C",
                ["XboxSeries.ShockBlue"] = "AB0CAF7858AB3A95C04384BC2239B37BE493E91FF266E0A7EA97F523AA7B9D3C",
                ["XboxSeries.VelocityGreen"] = "AB0CAF7858AB3A95C04384BC2239B37BE493E91FF266E0A7EA97F523AA7B9D3C",
                ["XboxSeries.Porsche75th"] = "AB0CAF7858AB3A95C04384BC2239B37BE493E91FF266E0A7EA97F523AA7B9D3C",
                ["XboxSeries.DaystrikeCamo"] = "AB0CAF7858AB3A95C04384BC2239B37BE493E91FF266E0A7EA97F523AA7B9D3C",
            };

        [Theory]
        [MemberData(nameof(SharedPairs))]
        public void ASharingColorwayStillRendersItsOwnBody(string colorway, string donor)
        {
            Assert.True(ExpectedBody.ContainsKey(colorway),
                $"{colorway} shares geometry but no body was recorded for it, so nothing "
                + "would notice if it started rendering the wrong shell");

            string actual = ResolvedMeshHash(colorway, "MainBody.obj");
            Assert.True(ExpectedBody[colorway] == actual,
                $"{colorway} resolves a body that is not the one it shipped with. It falls "
                + $"back to {donor}, which is the wrong shell for it.");
        }

        /// <summary>Resolves a mesh the way the loader does, a colorway's own
        /// folder first and its shared one second, and hashes the Wavefront
        /// text it finds.
        ///
        /// <para>The text, not the resource. Meshes are embedded compressed,
        /// and hashing the stored bytes would tie these anchors to the
        /// compression settings rather than to the shell each colorway
        /// renders.</para></summary>
        private static string ResolvedMeshHash(string modelName, string filename)
        {
            foreach (var candidate in Table().TryGetValue(modelName, out var shared)
                         ? new[] { modelName, shared }
                         : new[] { modelName })
            {
                var text = MeshText($".{candidate}.{MeshStem(filename)}");
                if (text != null)
                    return Convert.ToHexString(SHA256.HashData(text));
            }
            return null;
        }

        private static string MeshStem(string filename)
            => Path.GetFileNameWithoutExtension(filename) + MeshExtension;

        /// <summary>The extension meshes are embedded under. Compressed, so
        /// not the extension the art tree uses.</summary>
        private const string MeshExtension = ".objbr";

        private static byte[] MeshText(string suffix)
        {
            var assembly = typeof(ControllerModelBase).Assembly;
            string match = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            using var stream = assembly.GetManifestResourceStream(match);
            return Decompress(stream);
        }

        private static byte[] Decompress(Stream stream)
        {
            using var expanded = new MemoryStream();
            using (var brotli = new BrotliStream(stream, CompressionMode.Decompress))
                brotli.CopyTo(expanded);
            return expanded.ToArray();
        }

        /// <summary>Every mesh in the art tree reaches the assembly.
        ///
        /// <para>Meshes are packed at build time and embedded under a name
        /// the target computes, which is a second place a mesh can be lost
        /// without anything failing. It happened: each packed file is named
        /// for the resource it becomes, so its name carries dots, and MSBuild
        /// reads the segment before the extension as a culture when it
        /// matches one. The Switch 2 Pro's GL.obj packed to a name ending
        /// .Switch2Pro.GL.objbr, GL is Galician, and that mesh was routed
        /// into a satellite assembly. The build stayed green and the pad lost
        /// a button.</para>
        ///
        /// <para>Counting is not enough, since a lost mesh and a stray extra
        /// cancel out, so every file is matched by name.</para></summary>
        [Fact]
        public void EveryMeshInTheArtTreeIsEmbedded()
        {
            string root = ArtTree();
            Assert.True(root != null, "the 3DModels tree was not found beside the tests");

            var embedded = new HashSet<string>(
                typeof(ControllerModelBase).Assembly.GetManifestResourceNames()
                    .Where(n => n.EndsWith(MeshExtension, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*.obj", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '.');
                string expected = "PadForge._3DModels." +
                    relative.Substring(0, relative.Length - ".obj".Length) + MeshExtension;
                if (!embedded.Contains(expected))
                    missing.Add(expected);
            }

            Assert.True(missing.Count == 0,
                $"{missing.Count} meshes are in the art tree but not in the assembly:\n  "
                + string.Join("\n  ", missing.Take(20)));

            int onDisk = Directory.GetFiles(root, "*.obj", SearchOption.AllDirectories).Length;
            Assert.True(embedded.Count == onDisk,
                $"{embedded.Count} meshes are embedded but {onDisk} are in the art tree");
        }

        private static string ArtTree()
        {
            var d = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                var candidate = Path.Combine(d, "PadForge.App", "3DModels");
                if (Directory.Exists(candidate)) return candidate;
                d = Path.GetDirectoryName(d);
            }
            return null;
        }

        /// <summary>Duplicate meshes stay negligible.
        ///
        /// <para>This is what keeps the 87 MB from growing back. Copying a
        /// colorway folder is the natural way to start a new colorway and the
        /// natural way to reintroduce the duplication, and nothing else in a
        /// build would notice: one copied folder puts several megabytes
        /// straight back.</para>
        ///
        /// <para>What remains is 0.15 MB in the rumble motor meshes, which
        /// are identical across whole families rather than within one. A
        /// fallback reaches one colorway of the same family, so those are out
        /// of its range, and they are two small meshes. The budget is set just
        /// above them.</para></summary>
        [Fact]
        public void DuplicateMeshesStayNegligible()
        {
            const long Budget = 512 * 1024;

            var assembly = typeof(ControllerModelBase).Assembly;
            var seen = new Dictionary<string, string>();
            var clashes = new List<string>();
            long wasted = 0;

            foreach (var name in assembly.GetManifestResourceNames()
                         .Where(n => n.EndsWith(MeshExtension, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                byte[] text = Decompress(stream);
                string hash = Convert.ToHexString(SHA256.HashData(text));
                if (seen.TryGetValue(hash, out var first))
                {
                    wasted += text.Length;
                    clashes.Add($"{name} repeats {first} ({text.Length / 1024} KB)");
                }
                else
                {
                    seen[hash] = name;
                }
            }

            Assert.True(wasted <= Budget,
                $"{wasted / 1024} KB of meshes are embedded more than once, over the "
                + $"{Budget / 1024} KB budget:\n  " + string.Join("\n  ", clashes));
        }

        private static (string, string) Split(string modelName)
        {
            int dot = modelName.IndexOf('.');
            return (modelName.Substring(0, dot), modelName.Substring(dot + 1));
        }

        private static Dictionary<string, int> Inventory(ControllerModelBase model)
        {
            var parts = new Dictionary<string, int>();
            foreach (var field in typeof(ControllerModelBase)
                         .GetFields(BindingFlags.Public | BindingFlags.Instance)
                         .Where(f => f.FieldType == typeof(Model3DGroup)))
            {
                if (field.GetValue(model) is Model3DGroup g)
                    parts[field.Name] = Triangles(g);
            }
            return parts;
        }

        private static int Triangles(Model3DGroup group)
        {
            int n = 0;
            foreach (var child in group.Children)
            {
                if (child is GeometryModel3D geo && geo.Geometry is MeshGeometry3D mesh)
                    n += mesh.TriangleIndices.Count / 3;
                else if (child is Model3DGroup nested)
                    n += Triangles(nested);
            }
            return n;
        }
    }
}
