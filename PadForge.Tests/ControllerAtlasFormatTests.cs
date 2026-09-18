using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PadForge.Models3D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every appearance still finds every atlas it asks for.
    ///
    /// <para>Atlases ship in whichever format suits the image. An opaque one
    /// is a JPEG, because a 4096 shell costs 7 MB as PNG and 2 MB as JPEG at
    /// a quality no eye separates from the source. One carrying transparency
    /// stays PNG, because JPEG has no alpha and, on a 2048 decal that is
    /// mostly empty, costs more than the PNG it would replace.</para>
    ///
    /// <para>The model classes name an atlas without caring which format it
    /// is on disk, so the loader tries each candidate extension against the
    /// stem. That is a lookup by string, and a lookup by string fails
    /// quietly: a miss returns flat gray and the pad still renders, just
    /// wrong. Nothing in a build catches it. These tests do.</para>
    /// </summary>
    public class ControllerAtlasFormatTests
    {
        /// <summary>The exact gray the loader falls back to when it cannot
        /// find the atlas. It appears nowhere else in the codebase, so a
        /// material wearing it is a failed lookup and nothing else.</summary>
        private static readonly Color FallbackGray =
            (Color)ColorConverter.ConvertFromString("#5C5D60");

        public static TheoryData<string, string> EveryAppearance()
        {
            var data = new TheoryData<string, string>();
            foreach (var a in ControllerModelXboxSeries.AppearanceIds)
                data.Add("XboxSeries", a);
            foreach (var a in ControllerModelDualSense.AppearanceIds)
                data.Add("DualSense", a);
            foreach (var a in ControllerModelDS4.AppearanceIds)
                data.Add("DS4", a);
            foreach (var family in new[]
                     {
                         "DualSenseEdge", "Switch2Pro", "SteamDeck",
                         "SteamController", "SteamController2", "Xbox360",
                     })
                data.Add(family, null);
            return data;
        }

        [Theory]
        [MemberData(nameof(EveryAppearance))]
        public void EveryAppearanceResolvesEveryAtlas(string family, string appearance)
        {
            using var model = ControllerModelBase.Create(family, appearance, true);

            var missing = new List<Material>();
            foreach (var material in AllMaterials(model.model3DGroup))
                if (IsFallbackGray(material))
                    missing.Add(material);

            Assert.True(missing.Count == 0,
                $"{family}/{appearance ?? "default"}: {missing.Count} surfaces fell back to "
                + "flat gray, so the loader did not find their atlas");
        }

        /// <summary>A textured surface really is carrying a decoded image.
        ///
        /// <para>The test above proves nothing was missed. This one proves
        /// something was found: without it, a model whose every part happened
        /// to use a flat color would pass while carrying no art at all.</para>
        ///
        /// <para>The Valve pads and both Xbox legacy pads ship no atlas, so
        /// the demand for decoded art applies only where the assembly
        /// actually holds one. That is read from the manifest rather than
        /// listed here, so adding art to one of them turns the check on
        /// instead of leaving a stale exemption behind.</para>
        /// </summary>
        [Theory]
        [MemberData(nameof(EveryAppearance))]
        public void EveryAppearanceCarriesDecodedArt(string family, string appearance)
        {
            using var model = ControllerModelBase.Create(family, appearance, true);
            if (!FamilyShipsAnyAtlas(family)) return;

            int textured = 0;
            foreach (var material in AllMaterials(model.model3DGroup))
                foreach (var brush in Brushes(material))
                    if (brush is ImageBrush image && image.ImageSource != null)
                    {
                        Assert.True(image.ImageSource.Width >= 1024,
                            $"{family}/{appearance ?? "default"}: an atlas decoded at "
                            + $"{image.ImageSource.Width}px, so the wrong file was found");
                        textured++;
                    }

            Assert.True(textured > 0,
                $"{family}/{appearance ?? "default"}: no surface carries a decoded atlas");
        }

        /// <summary>Asking for one stem never answers with a longer one.
        ///
        /// <para>The lookup matches the tail of a resource name, and every
        /// model folder holds both Body and MainBody. Drop either the dot
        /// before the stem or the dot before the extension and "Body" starts
        /// matching "MainBody", which would hand a body atlas the mesh
        /// file.</para>
        /// </summary>
        [Fact]
        public void AStemNeverMatchesALongerStem()
        {
            var suffixes = TextureSuffixes("XboxSeries.Carbon", "Body.png");

            Assert.All(new[]
                {
                    "PadForge._3DModels.XboxSeries.Carbon.MainBody.png",
                    "PadForge._3DModels.XboxSeries.Carbon.MainBody.jpg",
                    "PadForge._3DModels.XboxSeries.Carbon.MainBodyBack.png",
                },
                name => Assert.False(EndsWithAny(name, suffixes),
                    $"{name} answered a request for Body"));

            Assert.True(EndsWithAny("PadForge._3DModels.XboxSeries.Carbon.Body.jpg", suffixes));
            Assert.True(EndsWithAny("PadForge._3DModels.XboxSeries.Carbon.Body.png", suffixes));
        }

        /// <summary>Every atlas embedded in the assembly is reachable by the
        /// stem its model class would ask for, in whichever format it
        /// shipped.</summary>
        [Fact]
        public void EveryEmbeddedAtlasIsReachableByItsStem()
        {
            var assembly = typeof(ControllerModelBase).Assembly;
            var atlases = assembly.GetManifestResourceNames()
                .Where(n => n.Contains("_3DModels.")
                            && (n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                || n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            Assert.NotEmpty(atlases);

            foreach (var name in atlases)
            {
                // PadForge._3DModels.<Family>.<Appearance>.<Stem>.<ext>
                var parts = name.Split('.');
                string stem = parts[^2];
                string model = string.Join('.', parts[2..^2]);

                var suffixes = TextureSuffixes(model, stem + ".png");
                Assert.True(atlases.Count(n => EndsWithAny(n, suffixes)) == 1,
                    $"{name} is not reachable as exactly one match for {model}/{stem}");
            }
        }

        private static bool FamilyShipsAnyAtlas(string family)
            => typeof(ControllerModelBase).Assembly.GetManifestResourceNames()
                .Any(n => n.Contains($"_3DModels.{family}.", StringComparison.OrdinalIgnoreCase)
                          && (n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                              || n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));

        private static string[] TextureSuffixes(string modelName, string filename)
            => (string[])typeof(ControllerModelBase)
                .GetMethod("TextureSuffixes", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { modelName, filename });

        private static bool EndsWithAny(string name, string[] suffixes)
            => (bool)typeof(ControllerModelBase)
                .GetMethod("EndsWithAny", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { name, suffixes });

        private static bool IsFallbackGray(Material material)
            => Brushes(material).Any(b => b is SolidColorBrush s && s.Color == FallbackGray);

        private static IEnumerable<Brush> Brushes(Material material)
        {
            switch (material)
            {
                case DiffuseMaterial d when d.Brush != null:
                    yield return d.Brush;
                    break;
                case MaterialGroup g:
                    foreach (var child in g.Children)
                        foreach (var b in Brushes(child))
                            yield return b;
                    break;
            }
        }

        private static IEnumerable<Material> AllMaterials(Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                if (child is GeometryModel3D geo)
                {
                    if (geo.Material != null) yield return geo.Material;
                    if (geo.BackMaterial != null) yield return geo.BackMaterial;
                }
                else if (child is Model3DGroup nested)
                {
                    foreach (var m in AllMaterials(nested))
                        yield return m;
                }
            }
        }
    }
}
