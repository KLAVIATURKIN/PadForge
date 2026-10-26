using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Logitech;
using PadForge.Engine.Common.MadCatz;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard screens (issue #519): the wire formats against the
    /// implementations that drive the hardware, the key decodes per device,
    /// the hand-off between LGS and the direct writers, the macro ordinals,
    /// the vendor gate and the settings.
    /// </summary>
    public class KeyboardScreenTests
    {
        private const int MonoW = KeyboardScreenFormats.MonoWidth, MonoH = KeyboardScreenFormats.MonoHeight;

        // ── The monochrome frame, against libg15 ──

        /// <summary>libg15's dumpPixmapIntoLCDFormat (netfab/libg15
        /// src/libg15.c), ported line for line, and writePixmapToLCD's
        /// report ID. Its input is a one-bit pixmap, 20 bytes a row, the
        /// leftmost pixel in the top bit, read past row 42 for the last band,
        /// so the pixmap here runs to 48 rows of zeros.</summary>
        private static byte[] Libg15Frame(bool[] lit)
        {
            const int width = 160, rowBytes = width / 8;
            var data = new byte[rowBytes * 48];
            for (int y = 0; y < MonoH; y++)
                for (int x = 0; x < width; x++)
                    if (lit[y * width + x]) data[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            var lcd = new byte[0x3E0];
            int outputOffset = 32, baseOffset = 0;
            for (int row = 0; row < 6; ++row)
            {
                for (int col = 0; col < width; ++col)
                {
                    int bit = col % 8;
                    lcd[outputOffset] = (byte)(
                        (((data[baseOffset] << bit) & 0x80) >> 7) |
                        (((data[baseOffset + rowBytes] << bit) & 0x80) >> 6) |
                        (((data[baseOffset + rowBytes * 2] << bit) & 0x80) >> 5) |
                        (((data[baseOffset + rowBytes * 3] << bit) & 0x80) >> 4) |
                        (((data[baseOffset + rowBytes * 4] << bit) & 0x80) >> 3) |
                        (((data[baseOffset + rowBytes * 5] << bit) & 0x80) >> 2) |
                        (((data[baseOffset + rowBytes * 6] << bit) & 0x80) >> 1) |
                        (((data[baseOffset + rowBytes * 7] << bit) & 0x80) >> 0));
                    ++outputOffset;
                    if (bit == 7) baseOffset++;
                }
                baseOffset += width - rowBytes;
            }
            lcd[0] = 0x03;
            return lcd;
        }

        /// <summary>A picture with the lit pixels dark, as the renderer draws
        /// the monochrome screen.</summary>
        private static byte[] MonoPicture(bool[] lit)
        {
            var bgra = new byte[MonoW * MonoH * 4];
            for (int p = 0; p < lit.Length; p++)
            {
                byte v = lit[p] ? (byte)0 : (byte)255;
                bgra[p * 4] = bgra[p * 4 + 1] = bgra[p * 4 + 2] = v;
                bgra[p * 4 + 3] = 255;
            }
            return bgra;
        }

        [Fact]
        public void TheMonochromeReport_IsLibg15sFrameByteForByte()
        {
            var random = new Random(519);
            var lit = new bool[MonoW * MonoH];
            for (int i = 0; i < lit.Length; i++) lit[i] = random.Next(2) == 0;
            var report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(MonoPicture(lit)));
            Assert.Equal(992, report.Length);
            Assert.Equal(Libg15Frame(lit), report);
        }

        [Fact]
        public void TheMonochromeReport_PutsABandsTopRowInBitZero()
        {
            var lit = new bool[MonoW * MonoH];
            lit[0] = true;                         // x 0, y 0: band 0, bit 0
            lit[9 * MonoW + 5] = true;             // x 5, y 9: band 1, bit 1
            lit[42 * MonoW + 159] = true;          // x 159, y 42: band 5, bit 2
            var report = KeyboardScreenFormats.ToG15Report(KeyboardScreenFormats.ToLgsMono(MonoPicture(lit)));
            Assert.Equal(0x03, report[0]);
            Assert.All(Enumerable.Range(1, 31), i => Assert.Equal(0, report[i]));
            Assert.Equal(0x01, report[32]);
            Assert.Equal(0x02, report[32 + 160 + 5]);
            Assert.Equal(0x04, report[32 + 5 * 160 + 159]);
            Assert.Equal(3, report.Skip(32).Count(b => b != 0));
        }

        [Fact]
        public void LgsMonochrome_LightsADarkPixelAt255()
        {
            var lit = new bool[MonoW * MonoH];
            lit[7] = true;
            var mono = KeyboardScreenFormats.ToLgsMono(MonoPicture(lit));
            Assert.Equal(160 * 43, mono.Length);
            Assert.Equal(0xFF, mono[7]);
            Assert.Equal(1, mono.Count(b => b != 0));
        }

        // ── The G19 frame, against G19USB ──

        [Fact]
        public void TheG19Frame_IsG19UsbsHeaderAndPixels()
        {
            var random = new Random(0x519);
            var bgra = new byte[320 * 240 * 4];
            random.NextBytes(bgra);
            var frame = KeyboardScreenFormats.ToG19Frame(bgra);
            Assert.Equal(154112, frame.Length);

            // G19USB G19Constants.LcdHeader: 16 fixed bytes, then each byte
            // its own index.
            byte[] start = { 0x10, 0x0F, 0x00, 0x58, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0x01, 0xEF, 0x00, 0x0F };
            Assert.Equal(start, frame.Take(16).ToArray());
            Assert.All(Enumerable.Range(16, 512 - 16), i => Assert.Equal((byte)i, frame[i]));

            // G19USB G19Helpers.ConvertBitmapToRGB565, JoinGreenAndBlue and
            // JoinRedAndGreen, x outer and y inner.
            var expected = new byte[320 * 240 * 2];
            for (int x = 0, pos = 0; x < 320; x++)
            {
                for (int y = 0; y < 240; y++)
                {
                    int i = (y * 320 + x) * 4;
                    int b = bgra[i], g = bgra[i + 1] >> 2, r = bgra[i + 2];
                    expected[pos++] = (byte)((g << 5) | (b >> 3));
                    expected[pos++] = (byte)((r & 0xF8) | (g >> 3));
                }
            }
            Assert.Equal(expected, frame.Skip(512).ToArray());
        }

        // ── The keys ──

        [Theory]
        [InlineData(KeyboardScreenDevices.G15)]
        [InlineData(KeyboardScreenDevices.Z10)]
        [InlineData(KeyboardScreenDevices.GamePanel)]
        public void TheG15sNineByteReport_DecodesItsKeys(ushort pid)
        {
            // hid-lg-g15 lg_g15_handle_lcd_menu_keys, libg15 processKeyEvent9Byte.
            byte[] report = { 0x02, 0, 0x80, 0, 0x80, 0, 0, 0, 0x80 };
            int bits = KeyboardScreenFormats.DecodeMonoKeys(pid, report);
            Assert.Equal((1 << 0) | (1 << 2) | (1 << KeyboardScreenFormats.MonoAppletKey), bits);
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(pid, new byte[] { 0x01, 0, 0x80, 0, 0, 0, 0, 0, 0 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(pid, new byte[] { 0x02, 0, 0x80 }));
        }

        [Fact]
        public void TheG15v2G510AndG13Reports_DecodeTheirKeys()
        {
            // hid-lg-g15 lg_g15_v2_event: byte 2, the applet key in bit 7.
            Assert.Equal((1 << 1) | (1 << KeyboardScreenFormats.MonoAppletKey),
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G15V2, new byte[] { 0x02, 0, 0x84, 0, 0 }));
            // lg_g510_event: report 3, byte 4, the applet key in bit 0.
            Assert.Equal((1 << 3) | (1 << KeyboardScreenFormats.MonoAppletKey),
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510, new byte[] { 0x03, 0, 0, 0, 0x11 }));
            Assert.Equal(1 << 0, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510Audio, new byte[] { 0x03, 0, 0, 0, 0x02 }));
            // The G13: report 1, byte 6.
            Assert.Equal(1 << 2, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G13, new byte[] { 0x01, 0, 0, 0, 0, 0, 0x08, 0 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G19, new byte[] { 0x02, 0, 0, 0, 0 }));
        }

        /// <summary>Windows pads every read to the longest input report of
        /// the collection, so a key report arrives with zeros after it and is
        /// still told by its ID.</summary>
        [Fact]
        public void APaddedWindowsRead_StillDecodes()
        {
            var padded = new byte[64];
            padded[0] = 0x03;
            padded[4] = 0x01;
            Assert.Equal(1 << KeyboardScreenFormats.MonoAppletKey,
                KeyboardScreenFormats.DecodeMonoKeys(KeyboardScreenDevices.G510, padded));
        }

        [Fact]
        public void TheG19sDisplayKeys_Decode()
        {
            // gnome15 keys.py, G19USB G19Keys.cs: byte 1 is 0x80.
            Assert.Equal((1 << KeyboardScreenFormats.G19Up) | (1 << KeyboardScreenFormats.G19Ok),
                KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x88, 0x80 }));
            Assert.Equal(1 << KeyboardScreenFormats.G19Settings, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01, 0x80 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01, 0x00 }));
            Assert.Equal(-1, KeyboardScreenFormats.DecodeG19Keys(new byte[] { 0x01 }));
        }

        [Fact]
        public void LgsButtonFlags_MapToTheRowsButtons()
        {
            Assert.Equal(11, KeyboardScreenFormats.LgsButtons.Length);
            Assert.Contains((0x0001, KeyboardScreenKind.Monochrome, KeyboardScreenFormats.MonoKey1), KeyboardScreenFormats.LgsButtons);
            Assert.Contains((0x4000, KeyboardScreenKind.Color, KeyboardScreenFormats.G19Menu), KeyboardScreenFormats.LgsButtons);
            Assert.DoesNotContain(KeyboardScreenFormats.LgsButtons,
                b => b.Kind == KeyboardScreenKind.Monochrome && b.Button == KeyboardScreenFormats.MonoAppletKey);
            Assert.DoesNotContain(KeyboardScreenFormats.LgsButtons,
                b => b.Kind == KeyboardScreenKind.Color && b.Button == KeyboardScreenFormats.G19Settings);
            Assert.All(KeyboardScreenFormats.LgsButtons,
                b => Assert.InRange(b.Button, 0, KeyboardScreenFormats.KeyCountOf(b.Kind) - 1));
        }

        /// <summary>A tap that starts and ends between two polls still reads
        /// pressed once, the G-key row's rule. Two engines on one screen both
        /// reach its row, and two screens stay apart.</summary>
        [Fact]
        public void ATapBetweenPolls_IsHeldForThePulse_AndTwoScreensStayApart()
        {
            string one = "test-" + Guid.NewGuid().ToString("N"), two = "test-" + Guid.NewGuid().ToString("N");
            try
            {
                KeyboardScreenKeys.Report(one, "hid", 1 << 2);
                KeyboardScreenKeys.Report(one, "hid", 0);
                var buttons = new bool[KeyboardScreenFormats.MaxKeys];
                long now = Environment.TickCount64;
                KeyboardScreenKeys.Read(one, buttons, now);
                Assert.True(buttons[2]);
                KeyboardScreenKeys.Read(one, buttons, now + KeyboardScreenKeys.PulseMs + 1);
                Assert.False(buttons[2]);

                // Two engines hold keys of one screen at once, and its row reads both.
                KeyboardScreenKeys.Report(one, "lgs", 1 << 0);
                KeyboardScreenKeys.Report(one, "hid", 1 << 4);
                KeyboardScreenKeys.Read(one, buttons, now + 10_000);
                Assert.True(buttons[0]);
                Assert.True(buttons[4]);
                KeyboardScreenKeys.Clear(one, "lgs");
                KeyboardScreenKeys.Read(one, buttons, now + 20_000);
                Assert.False(buttons[0]);
                Assert.True(buttons[4]);

                // A key on the other keyboard never reaches this one's row.
                KeyboardScreenKeys.Report(two, "hid", 1 << 1);
                KeyboardScreenKeys.Read(one, buttons, now + 30_000);
                Assert.False(buttons[1]);
                KeyboardScreenKeys.Read(two, buttons, now + 30_000);
                Assert.True(buttons[1]);
                Assert.False(buttons[4]);

                // A screen that left reads nothing.
                KeyboardScreenKeys.ClearScreen(two);
                KeyboardScreenKeys.Read(two, buttons, now + 40_000);
                Assert.DoesNotContain(true, buttons);
            }
            finally
            {
                KeyboardScreenKeys.ClearScreen(one);
                KeyboardScreenKeys.ClearScreen(two);
            }
        }

        // ── Which engine draws ──

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void TheMonochromeDirectWriter_StandsDownWhileLgsDraws(bool lgs, bool lgsSees, bool draws)
            => Assert.Equal(draws, KeyboardScreenHost.MonoDrawsDirectly(lgs, lgsSees));

        /// <summary>LGS takes one picture per kind, so it shows the first
        /// screen of each kind in id order, and the other screens' rows name
        /// that screen.</summary>
        [Fact]
        public void LgsShowsTheFirstScreenOfEachKind()
        {
            var g510 = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{bbbb0000-0000-0000-0000-000000000000}", "p1");
            var g15 = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{aaaa0000-0000-0000-0000-000000000000}", "p2");
            var g19 = KeyboardScreenInfo.G19(@"USB\VID_046D&PID_C229&MI_00\7&1&0&0000");
            var screens = new[] { g510, g19, g15, KeyboardScreenInfo.Arx() };
            // hid:046d:c222:... (the G15) sorts before hid:046d:c22d:... (the G510).
            Assert.Equal(g15, KeyboardScreenHost.Leader(screens, KeyboardScreenKind.Monochrome));
            Assert.Equal(g19, KeyboardScreenHost.Leader(screens, KeyboardScreenKind.Color));
            Assert.Null(KeyboardScreenHost.Leader(new[] { g510 }, KeyboardScreenKind.Color));
        }

        /// <summary>A keyboard's collections share its USB IDs and Windows'
        /// container, so they are one screen. Two keyboards are two, and the
        /// same keyboard found again is the same row.</summary>
        [Fact]
        public void EachKeyboardIsItsOwnScreen_ByItsContainer()
        {
            const string container = "{12345678-1234-1234-1234-123456789ABC}";
            var a = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, container, @"\\?\hid#vid_046d&pid_c222&mi_01&col01#x");
            var b = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, container, @"\\?\hid#vid_046d&pid_c222&mi_01&col02#x");
            var other = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{87654321-1234-1234-1234-123456789ABC}", "p");
            Assert.Equal(a, b);
            Assert.Equal(a.RowGuid, b.RowGuid);
            Assert.NotEqual(a.Id, other.Id);
            Assert.NotEqual(a.RowGuid, other.RowGuid);
            Assert.Equal("Logitech G15 Screen", a.Name);
            Assert.Equal("G15", a.Model);
            // No container named: the collection's own path keeps it apart.
            var lone = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G13, "", @"\\?\HID#VID_046D&PID_C21C#Y");
            Assert.Equal(@"hid:\\?\hid#vid_046d&pid_c21c#y", lone.Id);
            Assert.Equal(KeyboardScreenInfo.G19("X").RowGuid, KeyboardScreenInfo.G19("x").RowGuid);
        }

        /// <summary>A keyboard screen macro plays on the screens whose rows
        /// feed its slot, the Dreamcast macro's rule, and on no other.</summary>
        [Fact]
        public void AMacro_ReachesOnlyTheScreensThatFeedItsSlot()
        {
            var first = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G15, "{aaaa0000-0000-0000-0000-000000000000}", "p1");
            var second = KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{bbbb0000-0000-0000-0000-000000000000}", "p2");
            var arx = KeyboardScreenInfo.Arx();
            var slots = new Dictionary<Guid, HashSet<int>>
            {
                [first.RowGuid] = new() { 0 },
                [second.RowGuid] = new() { 1, 2 },
            };
            HashSet<int> SlotsOf(Guid row) => slots.TryGetValue(row, out var s) ? s : new HashSet<int>();
            var screens = new[] { first, second, arx };
            Assert.Equal(new[] { first }, KeyboardScreenService.Feeding(screens, 0, SlotsOf));
            Assert.Equal(new[] { second }, KeyboardScreenService.Feeding(screens, 2, SlotsOf));
            Assert.Empty(KeyboardScreenService.Feeding(screens, 5, SlotsOf));
        }

        [Fact]
        public void TheG19_IsBoundOnlyWhileLgsIsAway()
        {
            Assert.True(KeyboardScreenHost.G19MayBind(false));
            Assert.False(KeyboardScreenHost.G19MayBind(true));
        }

        // ── The test host loads no vendor library ──

        [Fact]
        public void EveryVendorLibrary_IsRefusedInTheTestHost()
        {
            Assert.True(VendorProgramGate.Refuses);
            Assert.Null(LogitechLcdLibrary.TryLoad(out string lcd));
            Assert.Equal("gate", lcd);
            Assert.Null(LogitechArxLibrary.TryLoad(out string arx));
            Assert.Equal("gate", arx);
            Assert.Null(Strike7Library.TryLoad(@"C:\nowhere\Strike7API.dll", out string strike7));
            Assert.Equal("gate", strike7);
        }

        // ── The keys row and the macros keep their numbers ──

        /// <summary>Each screen is a row of type 42 with its own kind's keys:
        /// five on a monochrome screen, eight on a G19, none on Arx Control
        /// or the S.T.R.I.K.E. 7, whose rows carry the screen alone.</summary>
        [Fact]
        public void EachScreenRow_IsDeviceType42_WithItsKindsKeys()
        {
            Assert.Equal(42, InputDeviceType.KeyboardScreenKeys);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.KeyboardScreenKeys));
            var cases = new (KeyboardScreenInfo Screen, int Keys, ushort Pid)[]
            {
                (KeyboardScreenInfo.Monochrome(KeyboardScreenDevices.G510, "{aaaa0000-0000-0000-0000-000000000000}", "p"), 5, 0x4B4D),
                (KeyboardScreenInfo.G19(@"USB\VID_046D&PID_C229&MI_00\7&1&0&0000"), 8, 0x4B43),
                (KeyboardScreenInfo.Arx(), 0, 0x4B41),
                (KeyboardScreenInfo.Strike7(), 0, 0x4B37),
            };
            foreach (var (screen, keys, pid) in cases)
            {
                using var row = new KeyboardScreenDevice(screen);
                Assert.Equal(InputDeviceType.KeyboardScreenKeys, row.GetInputDeviceType());
                Assert.Equal(keys, row.NumButtons);
                Assert.Equal(keys, row.GetDeviceObjects().Length);
                Assert.Equal(0x4B53, row.VendorId);
                Assert.Equal(pid, row.ProductId);
                Assert.Equal(screen.RowGuid, row.InstanceGuid);
                Assert.Equal(screen.Name, row.Name);
            }
        }

        /// <summary>The owner's rule (2026-10-25): the Settings page never
        /// sets anything for one device. The keyboard screens' dialogs and a
        /// Falcon's Calibrate Reach are on the device's panel on the Devices
        /// page, and the pad page holds only what belongs to a slot.</summary>
        [Fact]
        public void DeviceActions_LiveOnTheDevicesPage_NeverInSettings()
        {
            string settings = File.ReadAllText(RepoFile("PadForge.App", "Views", "SettingsPage.xaml"));
            string devices = File.ReadAllText(RepoFile("PadForge.App", "Views", "DevicesPage.xaml"));
            string pad = File.ReadAllText(RepoFile("PadForge.App", "Views", "PadPage.xaml"));
            foreach (string kind in new[] { "Monochrome", "Color", "Arx", "Strike7" })
                Assert.DoesNotContain("KeyboardScreens_Configure_" + kind, settings);
            Assert.DoesNotContain("Calibrate", settings);
            Assert.DoesNotContain("Falcon_Calibrate", pad);
            Assert.Contains("Click=\"KeyboardScreen_Click\"", devices);
            Assert.Contains("Click=\"FalconCalibrate_Click\"", devices);
        }

        [Fact]
        public void TheKeyboardScreenMacros_AppendAtTheTail()
        {
            Assert.Equal(58, (int)MacroActionType.ShowKeyboardScreen);
            Assert.Equal(59, (int)MacroActionType.KeyboardScreenMode);
            Assert.Equal(60, (int)MacroActionType.Stopwatch);
            Assert.Equal(61, (int)MacroActionType.PanKeyboardScreen);
            // Adaptive Trigger Effect and its Stop follow at 62 and 63 (#531).
            Assert.Equal(MacroActionType.PanKeyboardScreen, Enum.GetValues<MacroActionType>()[^3]);
            Assert.Equal(0, (int)KeyboardScreenMode.Off);
            Assert.Equal(8, (int)KeyboardScreenMode.Lightbar);
            Assert.Equal(KeyboardScreenMode.Lightbar, KeyboardScreenData.DefaultMode);
            Assert.Equal(3, (int)KeyboardScreenKind.Strike7);
        }

        /// <summary>Both dispatch loops send each action to its sink, the
        /// Show Dreamcast Screen seam's shape.</summary>
        [Fact]
        public void EachKeyboardScreenAction_SitsInBothEvaluationSwitches()
        {
            string source = File.ReadAllText(RepoFile("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));
            foreach (string call in new[]
            {
                "MacroKeyboardScreenShowSink(macro.PadIndex, action.ScreenKind, action.ScreenFrames,",
                "MacroKeyboardScreenModeSink(macro.PadIndex, action.ScreenKind, action.ScreenMode);",
                "MacroStopwatchSink(action.StopwatchAction);",
                "MacroKeyboardScreenPanSink(macro.PadIndex, action.PanDirection, action.PanStep);",
            })
                Assert.Equal(2, CountOf(source, call));
        }

        [Fact]
        public void TheKeyboardScreenActions_RoundTripThroughTheSettingsFile()
        {
            var action = new MacroAction
            {
                Type = MacroActionType.ShowKeyboardScreen,
                ScreenKind = KeyboardScreenKind.Arx,
                ScreenFrames = "AAAA,BBBB",
                ScreenFrameMs = 250,
                ScreenRepeat = 3,
                ScreenMode = KeyboardScreenMode.Stopwatch,
                StopwatchAction = KeyboardScreenStopwatchAction.Reset,
                PanDirection = KeyboardScreenPanDirection.Up,
                PanStep = 40,
            };
            var item = new MacroItem { Name = "screens" };
            item.Actions.Add(action);
            var data = SettingsService.BuildMacroDataForMacro(item, 0);
            var back = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null).Actions[0];
            Assert.Equal(KeyboardScreenKind.Arx, back.ScreenKind);
            Assert.Equal("AAAA,BBBB", back.ScreenFrames);
            Assert.Equal(250, back.ScreenFrameMs);
            Assert.Equal(3, back.ScreenRepeat);
            Assert.Equal(KeyboardScreenMode.Stopwatch, back.ScreenMode);
            Assert.Equal(KeyboardScreenStopwatchAction.Reset, back.StopwatchAction);
            Assert.Equal(KeyboardScreenPanDirection.Up, back.PanDirection);
            Assert.Equal(40, back.PanStep);

            // An action from a file written before #519 takes the defaults.
            var fresh = new MacroAction();
            Assert.Equal(KeyboardScreenData.DefaultMode, fresh.ScreenMode);
            Assert.Equal(1000, fresh.ScreenFrameMs);
            Assert.Equal(MacroAction.DefaultPanStep, fresh.PanStep);
            Assert.Equal(KeyboardScreenService.MinFrameMs, new MacroAction { ScreenFrameMs = 1 }.ScreenFrameMs);
        }

        // ── Settings ──

        /// <summary>Each screen's choices are kept by its row, BlissBoxPortData's
        /// cleanup: an entry with no readable row, one left at every default
        /// and a second entry for one row go, and an unknown mode is put back
        /// to the default.</summary>
        [Fact]
        public void TheScreensList_IsCleanedOnLoad()
        {
            Guid g15 = Guid.NewGuid(), g19 = Guid.NewGuid(), arx = Guid.NewGuid();
            var list = KeyboardScreenData.Normalize(new[]
            {
                null,
                new KeyboardScreenData { Device = g19.ToString("N"), Mode = KeyboardScreenMode.Clock, PanX = -5 },
                new KeyboardScreenData { Device = g19.ToString("D"), Mode = KeyboardScreenMode.Picture },
                new KeyboardScreenData { Device = "not a device", Mode = KeyboardScreenMode.Clock },
                new KeyboardScreenData { Device = arx.ToString("D"), Mode = (KeyboardScreenMode)42, Picture = "map.png" },
                new KeyboardScreenData { Device = g15.ToString("D") },
            });
            Assert.Equal(2, list.Count);
            Assert.Equal(g19.ToString("D"), list[0].Device);
            Assert.Equal(KeyboardScreenMode.Clock, list[0].Mode);
            Assert.Equal(0, list[0].PanX);
            Assert.Equal(arx.ToString("D"), list[1].Device);
            Assert.Equal(KeyboardScreenData.DefaultMode, list[1].Mode);
            Assert.Empty(KeyboardScreenData.Normalize(null));
        }

        [Fact]
        public void TheScreens_RoundTripThroughTheSettingsXml()
        {
            Guid g510 = Guid.NewGuid(), strike7 = Guid.NewGuid();
            var data = new AppSettingsData
            {
                KeyboardScreensEnabled = true,
                KeyboardScreens = new[]
                {
                    new KeyboardScreenData { Device = g510.ToString("D"), Mode = KeyboardScreenMode.PlayTime },
                    new KeyboardScreenData { Device = strike7.ToString("D"), Mode = KeyboardScreenMode.Picture, Picture = "maps\\world.png", PanX = 300, PanY = 120 },
                },
                DefaultProfileKeyboardScreenPicture = "default.png",
                Strike7LibraryPath = "C:\\Tools\\Strike7API.dll",
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            string xml = writer.ToString();
            Assert.Contains("<KeyboardScreens>", xml);
            Assert.Contains("Device=\"" + strike7.ToString("D") + "\"", xml);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(xml));
            Assert.True(back.KeyboardScreensEnabled);
            Assert.Equal(2, back.KeyboardScreens.Length);
            Assert.Equal(KeyboardScreenMode.PlayTime, back.KeyboardScreens[0].Mode);
            Assert.Equal("maps\\world.png", back.KeyboardScreens[1].Picture);
            Assert.Equal(300, back.KeyboardScreens[1].PanX);
            Assert.Equal(120, back.KeyboardScreens[1].PanY);
            Assert.Equal("default.png", back.DefaultProfileKeyboardScreenPicture);
            Assert.Equal("C:\\Tools\\Strike7API.dll", back.Strike7LibraryPath);
        }

        [Fact]
        public void TheSwitch_IsOffByDefault_AndFeedsTheEngine()
        {
            var vm = new SettingsViewModel();
            Assert.False(vm.KeyboardScreensEnabled);
            vm.KeyboardScreensEnabled = true;
            try
            {
                Assert.True(KeyboardScreensRuntime.Enabled);
                vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.KeyboardScreensEnabled));
                Assert.False(vm.KeyboardScreensEnabled);
                Assert.False(KeyboardScreensRuntime.Enabled);
            }
            finally
            {
                vm.KeyboardScreensEnabled = false;
            }
            string settings = File.ReadAllText(RepoFile("PadForge.App", "Services", "SettingsService.cs"));
            Assert.Contains("vm.KeyboardScreensEnabled = appSettings.KeyboardScreensEnabled;", settings);
            Assert.Contains("KeyboardScreensEnabled = vm.KeyboardScreensEnabled,", settings);
            Assert.Contains("vm.KeyboardScreens = KeyboardScreenData.Normalize(appSettings.KeyboardScreens);", settings);
        }

        // ── The stopwatch, a show and the pictures ──

        [Fact]
        public void TheStopwatch_StartsPausesAndResets()
        {
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.Reset, 0);
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 1_000);
            Assert.Equal(TimeSpan.FromSeconds(4), KeyboardScreenService.StopwatchTime(5_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 5_000);
            Assert.Equal(TimeSpan.FromSeconds(4), KeyboardScreenService.StopwatchTime(60_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.StartPause, 60_000);
            Assert.Equal(TimeSpan.FromSeconds(6), KeyboardScreenService.StopwatchTime(62_000));
            KeyboardScreenService.Stopwatch(KeyboardScreenStopwatchAction.Reset, 62_000);
            Assert.Equal(TimeSpan.Zero, KeyboardScreenService.StopwatchTime(90_000));
            Assert.Equal("1:05", KeyboardScreenService.FormatStopwatch(TimeSpan.FromSeconds(65)));
            Assert.Equal("1:00:05", KeyboardScreenService.FormatStopwatch(TimeSpan.FromSeconds(3605)));
        }

        [Fact]
        public void AShow_PlaysEachFrameForItsTime_ThenEnds()
        {
            byte[] a = { 1 }, b = { 2 };
            var show = new KeyboardScreenShow(new() { a, b }, 200, 2, 1_000);
            Assert.Same(a, show.Frame(1_000));
            Assert.Same(b, show.Frame(1_250));
            Assert.Same(a, show.Frame(1_450));
            Assert.Same(b, show.Frame(1_799));
            Assert.Null(show.Frame(1_800));
            // A frame time under the floor is held to it.
            var fast = new KeyboardScreenShow(new() { a }, 1, 1, 0);
            Assert.Same(a, fast.Frame(KeyboardScreenService.MinFrameMs - 1));
            Assert.Null(fast.Frame(KeyboardScreenService.MinFrameMs));
        }

        [Fact]
        public void TheRenderer_DrawsEachKindAtItsSize()
            => RunSta(() =>
            {
                foreach (var kind in KeyboardScreenFormats.Kinds)
                {
                    var (w, h) = KeyboardScreenFormats.SizeOf(kind);
                    var text = KeyboardScreenRenderer.RenderText("12:34", kind);
                    Assert.Equal(w * h * 4, text.Length);
                    if (KeyboardScreenFormats.IsMonochrome(kind))
                    {
                        // Pure black and white, some of each.
                        Assert.All(Enumerable.Range(0, w * h), p => Assert.True(text[p * 4] is 0 or 255));
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] == 0);
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] == 255);
                    }
                    else
                    {
                        // Light text on black.
                        Assert.Equal(0, text[0]);
                        Assert.Contains(Enumerable.Range(0, w * h), p => text[p * 4] > 200);
                    }
                }
            });

        [Fact]
        public void TheLightbarColor_FillsAColorScreen_AndDithersAMonochromeOne()
        {
            var color = KeyboardScreenRenderer.RenderColor(0x10, 0x80, 0xF0, KeyboardScreenKind.Color);
            Assert.Equal(0xF0, color[0]);
            Assert.Equal(0x80, color[1]);
            Assert.Equal(0x10, color[2]);
            Assert.Equal(255, color[3]);

            int Dark(byte r, byte g, byte b)
            {
                var mono = KeyboardScreenRenderer.RenderColor(r, g, b, KeyboardScreenKind.Monochrome);
                return Enumerable.Range(0, MonoW * MonoH).Count(p => mono[p * 4] == 0);
            }
            Assert.Equal(0, Dark(255, 255, 255));
            Assert.Equal(MonoW * MonoH, Dark(0, 0, 0));
            int half = Dark(128, 128, 128);
            Assert.InRange(half, MonoW * MonoH * 4 / 10, MonoW * MonoH * 6 / 10);
        }

        /// <summary>The Lightbar mode shows the lowest slot's color among the
        /// pads still on the slot their color came from, and forgets a pad
        /// that left. The pads here are this test's own, so a color the
        /// effects path records meanwhile is never one it reads.</summary>
        [Fact]
        public void TheLightbarColor_IsTheLowestSlotsStillOnIt_AndAPadThatLeftIsForgotten()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var where = new Dictionary<Guid, int> { [a] = 2, [b] = 1, [c] = 3 };
            bool OnItsSlot(Guid device, int slot) => where.TryGetValue(device, out int now) && now == slot;

            LightbarColors.Record(2, a, 1, 2, 3);
            LightbarColors.Record(1, b, 4, 5, 6);
            LightbarColors.Record(3, c, 7, 8, 9);
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out var color));
            Assert.Equal(((byte)4, (byte)5, (byte)6), color);

            // b moves to slot 0: its color came from slot 1, so it is gone.
            where[b] = 0;
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)1, (byte)2, (byte)3), color);
            where[b] = 1;
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)1, (byte)2, (byte)3), color);

            // A new color on the slot takes over again.
            LightbarColors.Record(1, b, 10, 20, 30);
            Assert.True(LightbarColors.TryGetLowest(OnItsSlot, out color));
            Assert.Equal(((byte)10, (byte)20, (byte)30), color);

            where.Clear();
            Assert.False(LightbarColors.TryGetLowest(OnItsSlot, out _));
        }

        /// <summary>A screen whose row feeds slots follows the lowest of
        /// those slots, so player two's keyboard shows player two's color.
        /// A screen that feeds none follows the lowest slot of all.</summary>
        [Fact]
        public void TheLightbarColor_FollowsTheSlotsTheScreenFeeds()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var where = new Dictionary<Guid, int> { [a] = 1, [b] = 2 };
            bool OnItsSlot(Guid device, int slot) => where.TryGetValue(device, out int now) && now == slot;
            LightbarColors.Record(1, a, 1, 2, 3);
            LightbarColors.Record(2, b, 4, 5, 6);
            try
            {
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 2, out var color));
                Assert.Equal(((byte)4, (byte)5, (byte)6), color);
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 1 || slot == 2, out color));
                Assert.Equal(((byte)1, (byte)2, (byte)3), color);
                Assert.False(LightbarColors.TryGetLowest(OnItsSlot, slot => slot == 7, out _));
                Assert.True(LightbarColors.TryGetLowest(OnItsSlot, null, out color));
                Assert.Equal(((byte)1, (byte)2, (byte)3), color);
            }
            finally
            {
                where.Clear();
                LightbarColors.TryGetLowest(OnItsSlot, out _);
            }
        }

        [Fact]
        public void APicture_LargerThanTheScreen_PansToItsEdgesAndNoFurther()
            => RunSta(() =>
            {
                var pixels = new byte[600 * 400 * 4];
                for (int p = 0; p < 600 * 400; p++) pixels[p * 4 + 3] = 255;
                // A white column at x 500 marks where the window has moved.
                for (int y = 0; y < 400; y++) { int i = (y * 600 + 500) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 255; }
                var picture = System.Windows.Media.Imaging.BitmapSource.Create(600, 400, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, pixels, 600 * 4);
                Assert.Equal((120, 80), KeyboardScreenRenderer.PanLimits(picture, KeyboardScreenKind.Strike7));
                Assert.Equal((280, 160), KeyboardScreenRenderer.PanLimits(picture, KeyboardScreenKind.Color));
                Assert.Equal((0, 0), KeyboardScreenRenderer.PanLimits(null, KeyboardScreenKind.Color));

                // At the far edge the column sits at x 500 - 120 = 380.
                var frame = KeyboardScreenRenderer.RenderWindow(picture, KeyboardScreenKind.Strike7, 999, 999);
                Assert.True(frame[(10 * 480 + 380) * 4] > 200);
                Assert.True(frame[(10 * 480 + 379) * 4] < 50);
            });

        // ── Helpers ──

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "render run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
