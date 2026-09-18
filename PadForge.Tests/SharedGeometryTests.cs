using System;
using System.Collections.Generic;
using System.IO;
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
                            && n.EndsWith(".obj", StringComparison.OrdinalIgnoreCase));

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
                         .Where(n => n.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                string hash = Convert.ToHexString(SHA256.HashData(ms.ToArray()));
                if (seen.TryGetValue(hash, out var first))
                {
                    wasted += ms.Length;
                    clashes.Add($"{name} repeats {first} ({ms.Length / 1024} KB)");
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
