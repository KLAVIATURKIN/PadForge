using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A recipe button inserts a formula, and the formula has to hold in the
    /// value space of the editor it sits in. A mapping row's sources rest at 0:
    /// a button is 0 or 1, a stick -1..1, a trigger 0..1. A macro Custom
    /// Expression reads a stick as 0..1, resting at 0.5.
    ///
    /// <para>The "Axis past 50%" recipe was written for the macro space
    /// (abs(a - 0.5) &gt; 0.25) and copied verbatim into the mapping editor,
    /// where every source at rest sits 0.5 from 0.5, so a row using it held its
    /// button, pinned its stick at full and pulled its trigger with nothing
    /// touched.</para>
    /// </summary>
    public class FormulaRecipeValueSpaceTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>The Tag of the recipe button whose Content binds
        /// <paramref name="key"/> and whose Style is <paramref name="style"/>.
        /// Attribute text escapes '&gt;', so an element ends at its first raw '&gt;'.</summary>
        private static string RecipeFormula(string key, string style)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));
            var buttons = Regex.Matches(xaml, @"<Button\b[^>]*>")
                .Select(m => m.Value)
                .Where(b => b.Contains("{Binding " + key + ",") && b.Contains("{StaticResource " + style + "}"))
                .ToList();
            Assert.Single(buttons);
            var tag = Regex.Match(buttons[0], "Tag=\"([^\"]*)\"");
            Assert.True(tag.Success);
            return WebUtility.HtmlDecode(tag.Groups[1].Value);
        }

        private static float Eval(string formula, float a)
        {
            var compiled = MappingExpression.Compile(formula);
            Assert.True(compiled.IsValid, formula);
            return compiled.Evaluate(new[] { a });
        }

        [Fact]
        public void AxisPastHalf_OnAMappingRow_IsFalseAtRest_AndTrueBeyondHalfEitherWay()
        {
            string f = RecipeFormula("Macro_Expression_Recipe_AxisOverHalf", "FormulaPresetStyle");
            Assert.Equal(0f, Eval(f, 0f));
            Assert.Equal(0f, Eval(f, 0.25f));
            Assert.Equal(0f, Eval(f, -0.25f));
            Assert.Equal(1f, Eval(f, 0.75f));
            Assert.Equal(1f, Eval(f, -0.75f));
            // A button source is 0 or 1: it passes straight through.
            Assert.Equal(1f, Eval(f, 1f));
        }

        [Fact]
        public void AxisPastHalf_InAMacroExpression_IsFalseForACenteredStick_AndTrueAtFullPush()
        {
            string f = RecipeFormula("Macro_Expression_Recipe_AxisOverHalf", "MacroFormulaPresetStyle");
            // A centered stick reads 32768 / 65535 in the macro space.
            Assert.True(Eval(f, 32768f / 65535f) < 0.5f);
            Assert.True(Eval(f, 1f) >= 0.5f);
            Assert.True(Eval(f, 0f) >= 0.5f);
        }
    }
}
