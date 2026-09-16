using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The formula warning flag and the footer text answer the same question,
    /// so they must answer it the same way.
    ///
    /// <para>The flag compared the COUNT of referenced letters against the
    /// number of defined variables, while the footer compared each letter's
    /// POSITION. A formula that uses only "z" with one variable defined
    /// referenced one letter against one variable, so the flag said nothing was
    /// wrong while the footer named z as having no source. A user reaches that
    /// state by deleting a variable and leaving the formula alone.</para>
    /// </summary>
    public sealed class CustomExpressionWarningParityTests
    {
        private static MacroItem WithFormula(string formula, int variables)
        {
            var macro = new MacroItem
            {
                Name = "formula",
                TriggerMode = MacroTriggerMode.CustomExpression,
                TriggerExpression = formula,
            };
            macro.TriggerExpressionVariables.Clear();
            for (int i = 0; i < variables; i++)
                macro.TriggerExpressionVariables.Add(new MacroExpressionVariable());
            // The collection is mutated directly, so re-assign the formula to
            // recompute the derived state the editor binds to.
            macro.TriggerExpression = formula;
            return macro;
        }

        /// <summary>A sparse letter past the defined count is unsourced, and
        /// both surfaces must say so.</summary>
        [Fact]
        public void ASparseLetterRaisesTheWarningFlagAndTheFooter()
        {
            var macro = WithFormula("z", variables: 1);
            Assert.True(macro.IsCustomExpressionWarning,
                "the flag missed an unsourced sparse letter the footer reports");
            Assert.Contains("z", macro.CustomExpressionStatus);
        }

        /// <summary>Positive control: a formula whose references all have
        /// sources raises neither.</summary>
        [Fact]
        public void AFullySourcedFormulaRaisesNeither()
        {
            var macro = WithFormula("a + b", variables: 2);
            Assert.False(macro.IsCustomExpressionWarning);
            Assert.DoesNotContain("⚠", macro.CustomExpressionStatus);
        }

        /// <summary>An indexed reference past the defined count behaves the
        /// same on both surfaces.</summary>
        [Fact]
        public void AnOutOfRangeIndexedReferenceAgreesOnBothSurfaces()
        {
            var macro = WithFormula("s[3]", variables: 1);
            Assert.True(macro.IsCustomExpressionWarning);
            Assert.Contains("s[3]", macro.CustomExpressionStatus);
        }

        /// <summary>The two surfaces agree across a spread of formulas.</summary>
        [Theory]
        [InlineData("a", 1, false)]
        [InlineData("b", 1, true)]
        [InlineData("a + z", 2, true)]
        [InlineData("a + b", 2, false)]
        [InlineData("s[0]", 1, false)]
        [InlineData("s[1]", 1, true)]
        public void TheFlagAndTheFooterNeverDisagree(string formula, int variables, bool expectWarning)
        {
            var macro = WithFormula(formula, variables);
            bool footerWarns = macro.CustomExpressionStatus.Contains("⚠");
            Assert.Equal(expectWarning, macro.IsCustomExpressionWarning);
            Assert.Equal(macro.IsCustomExpressionWarning, footerWarns);
        }
    }
}
