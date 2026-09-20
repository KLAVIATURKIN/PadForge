using System.IO;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The saved button and axis positions index name tables and state arrays
    /// in every reader they have, and those readers check the top of the range
    /// and not the bottom. Every list PadForge builds is in range. A settings
    /// file edited by hand, or damaged on disk, is the one source that is not,
    /// and a -1 in it reached <c>names[-1]</c> in the mapping picker.
    ///
    /// <para>The lists are cleaned where they enter, in the property setter,
    /// so the fix does not depend on finding every reader.</para>
    /// </summary>
    public class SavedCapabilityPositionsTests
    {
        [Fact]
        public void PositionsOutsideTheState_AreDroppedOnTheWayIn()
        {
            var ud = new UserDevice
            {
                CapAxisIndices = new[] { -1, 0, 3, CustomInputState.MaxAxis, 4 },
                CapButtonIndices = new[] { 0, -7, 16, CustomInputState.MaxButtons, int.MinValue },
            };

            Assert.Equal(new[] { 0, 3, 4 }, ud.CapAxisIndices);
            Assert.Equal(new[] { 0, 16 }, ud.CapButtonIndices);
        }

        /// <summary>The last slot is a real slot. A bound that is off by one
        /// would take the highest button a device has away from it.</summary>
        [Fact]
        public void TheLastSlotOfEachRange_IsKept()
        {
            var ud = new UserDevice
            {
                CapAxisIndices = new[] { CustomInputState.MaxAxis - 1 },
                CapButtonIndices = new[] { CustomInputState.MaxButtons - 1 },
            };

            Assert.Equal(new[] { CustomInputState.MaxAxis - 1 }, ud.CapAxisIndices);
            Assert.Equal(new[] { CustomInputState.MaxButtons - 1 }, ud.CapButtonIndices);
        }

        /// <summary>Null means the device was never observed, and an empty
        /// list means it was observed to have none. Readers treat the two
        /// differently, so cleaning must not turn one into the other. A list
        /// with nothing wrong comes back as the same array.</summary>
        [Fact]
        public void NullStaysNull_EmptyStaysEmpty_AndACleanListIsNotCopied()
        {
            var clean = new[] { 0, 1, 5 };
            var ud = new UserDevice { CapAxisIndices = null, CapButtonIndices = clean };
            Assert.Null(ud.CapAxisIndices);
            Assert.Same(clean, ud.CapButtonIndices);

            ud.CapAxisIndices = new int[0];
            Assert.NotNull(ud.CapAxisIndices);
            Assert.Empty(ud.CapAxisIndices);
        }

        /// <summary>The route the bad values actually take: a settings file.
        /// The serializer builds the array and hands it to the setter whole,
        /// so the setter is where a loaded list is cleaned.</summary>
        [Fact]
        public void ALoadedSettingsRecord_ComesBackClean()
        {
            const string xml =
                "<UserDevice>" +
                "<CapAxisIndices><int>-1</int><int>2</int></CapAxisIndices>" +
                "<CapButtonIndices><int>4</int><int>-3</int><int>9</int></CapButtonIndices>" +
                "</UserDevice>";

            var serializer = new XmlSerializer(typeof(UserDevice));
            UserDevice ud;
            using (var reader = new StringReader(xml))
                ud = (UserDevice)serializer.Deserialize(reader);

            Assert.Equal(new[] { 2 }, ud.CapAxisIndices);
            Assert.Equal(new[] { 4, 9 }, ud.CapButtonIndices);
        }
    }
}
