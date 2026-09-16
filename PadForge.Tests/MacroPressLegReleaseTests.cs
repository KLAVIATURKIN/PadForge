using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A press leg that was cut short still owes its key-up.
    ///
    /// <para>The leg sends its down on the tick it becomes current and its up
    /// when the duration elapses. Every other way a run can end retired the
    /// pending entry without that up, so a macro stopped mid-press left the
    /// key down in the operating system with nothing left to release it. The
    /// engine stop was the plainest case: the method whose whole purpose is to
    /// strand nothing cleared the pending set wholesale.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroPressLegReleaseTests
    {
        private static HashSet<MacroAction> Pending(InputManager im)
        {
            var f = typeof(InputManager).GetField("_pressDownSent",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return (HashSet<MacroAction>)f.GetValue(im);
        }

        private static void ReleasePendingPress(InputManager im, MacroAction a)
        {
            var m = typeof(InputManager).GetMethod("ReleasePendingPress",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            m.Invoke(im, new object[] { a });
        }

        private static MacroAction KeyPress(int vk) => new MacroAction
        {
            Type = MacroActionType.KeyPress,
            KeyCode = vk,
            DurationMs = 5000,
        };

        /// <summary>The owed up retires the entry. Whether the keystroke
        /// reaches the operating system is not observable from a test, so the
        /// pin is that the leg is no longer pending and therefore no longer
        /// held by this engine.</summary>
        [Fact]
        public void ReleasingAPendingPressRetiresIt()
        {
            var im = new InputManager();
            var action = KeyPress(0x41);
            Pending(im).Add(action);

            ReleasePendingPress(im, action);

            Assert.DoesNotContain(action, Pending(im));
        }

        /// <summary>Releasing one leg does not retire another that holds the
        /// same key. The operating system keeps a single logical state per
        /// key, so the up is withheld while a second holder still wants it
        /// down, which would otherwise trade a stuck key for a dropped one.</summary>
        [Fact]
        public void AnotherHolderOfTheSameKeyIsLeftAlone()
        {
            var im = new InputManager();
            var first = KeyPress(0x41);
            var second = KeyPress(0x41);
            Pending(im).Add(first);
            Pending(im).Add(second);

            ReleasePendingPress(im, first);

            Assert.DoesNotContain(first, Pending(im));
            Assert.Contains(second, Pending(im));
        }

        /// <summary>The engine stop drains every owed up instead of dropping
        /// the whole set. Whether a keystroke reaches the operating system is
        /// not observable from here, and clearing the set empties it either
        /// way, so the drain itself is pinned against the source the way this
        /// project pins its other uninstrumentable contracts.</summary>
        [Fact]
        public void TheEngineStopDrainsRatherThanDropsTheOwedReleases()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d.FullName,
                "PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));

            int stop = src.IndexOf("internal void ReleaseAllLatchedMacroKeys()", StringComparison.Ordinal);
            Assert.True(stop > 0, "the engine stop is gone");
            int end = src.IndexOf("\n        }", stop, StringComparison.Ordinal);
            string body = src.Substring(stop, end - stop);

            Assert.Contains("ReleasePendingPress(", body);
            // And the drain runs BEFORE the set is emptied, or it drains nothing.
            Assert.True(body.IndexOf("ReleasePendingPress(", StringComparison.Ordinal)
                      < body.IndexOf("_pressDownSent.Clear();", StringComparison.Ordinal),
                "the set is cleared before the owed releases are drained");
        }

        [Fact]
        public void TheEngineStopEmptiesThePendingSet()
        {
            var im = new InputManager();
            Pending(im).Add(KeyPress(0x41));
            Pending(im).Add(KeyPress(0x42));
            var mouse = new MacroAction { Type = MacroActionType.MouseButtonPress, DurationMs = 5000 };
            Pending(im).Add(mouse);
            Assert.Equal(3, Pending(im).Count);

            im.ReleaseAllLatchedMacroKeys();

            Assert.Empty(Pending(im));
        }

        /// <summary>Ending a run drains the legs that run owns, and leaves
        /// another macro's alone.</summary>
        [Fact]
        public void EndingARunDrainsOnlyItsOwnLegs()
        {
            var im = new InputManager();
            var mine = KeyPress(0x41);
            var theirs = KeyPress(0x42);

            var macro = new MacroItem { Name = "mine" };
            macro.Actions.Add(mine);

            Pending(im).Add(mine);
            Pending(im).Add(theirs);

            var end = typeof(InputManager).GetMethod("EndMacroRun",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(end);
            end.Invoke(im, new object[] { macro });

            Assert.DoesNotContain(mine, Pending(im));
            Assert.Contains(theirs, Pending(im));
        }

        /// <summary>Positive control: a leg that was never pending is not
        /// disturbed, so the helper does not send unowed releases.</summary>
        [Fact]
        public void ALegThatWasNeverPressedOwesNothing()
        {
            var im = new InputManager();
            var action = KeyPress(0x41);

            ReleasePendingPress(im, action);

            Assert.Empty(Pending(im));
        }
    }
}
