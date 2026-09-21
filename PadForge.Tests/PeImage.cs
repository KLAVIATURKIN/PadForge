using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PadForge.Tests
{
    /// <summary>
    /// The export and import tables of a PE image, read from its bytes.
    ///
    /// <para>A search of the file's text for a function name finds that name
    /// wherever it sits. It finds it in the export name table, and it finds
    /// it just as well in the symbol table of a DLL that was not stripped,
    /// which names every function whether it is exported or not. So a text
    /// search cannot say that a function is exported. This follows the
    /// structures the Windows loader follows: the data directory, the export
    /// directory, and for each name its ordinal and the address behind it
    /// (IMAGE_EXPORT_DIRECTORY, winnt.h).</para>
    ///
    /// <para>Every offset is checked against the file before it is read, in
    /// arithmetic that cannot wrap. A table has to sit in bytes the file
    /// really holds, since a section's virtual size can promise more, and
    /// an export's address has to land inside a section. Anything that does not
    /// add up throws <see cref="InvalidDataException"/>, so a damaged table
    /// fails a test and never passes as an empty one. A data directory of
    /// zero is the one honest way to have no table, and it reads as none.</para>
    /// </summary>
    internal sealed class PeImage
    {
        private readonly byte[] _image;
        private readonly List<(uint Rva, uint VirtualSize, uint RawOffset, uint RawSize)> _sections
            = new List<(uint, uint, uint, uint)>();
        private readonly (uint Rva, uint Size)[] _directories = new (uint, uint)[16];
        private readonly uint _directoryTable;
        private readonly uint _sectionTable;

        private const int ExportDirectory = 0;
        private const int ImportDirectory = 1;
        private const int DelayImportDirectory = 13;

        public ushort Machine { get; }
        public int SectionCount => _sections.Count;

        public PeImage(byte[] image)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            if (U16(0) != 0x5A4D) throw Bad("no MZ header");
            uint pe = U32(0x3C);
            if (U32(pe) != 0x00004550) throw Bad("no PE signature");
            Machine = U16(Add(pe, 4));
            ushort sectionCount = U16(Add(pe, 6));
            ushort optionalSize = U16(Add(pe, 20));
            uint optional = Add(pe, 24);

            ushort magic = U16(optional);
            uint countAt;
            if (magic == 0x20B) { countAt = Add(optional, 108); _directoryTable = Add(optional, 112); }
            else if (magic == 0x10B) { countAt = Add(optional, 92); _directoryTable = Add(optional, 96); }
            else throw Bad("unknown optional header magic 0x" + magic.ToString("X"));

            uint count = U32(countAt);
            if (count > 16) throw Bad("more than 16 data directories");
            if ((ulong)_directoryTable + count * 8 > (ulong)optional + optionalSize)
                throw Bad("the data directories run past the optional header");
            for (uint i = 0; i < count; i++)
                _directories[i] = (U32(_directoryTable + i * 8), U32(_directoryTable + i * 8 + 4));

            // IMAGE_SECTION_HEADER: VirtualSize at 8, VirtualAddress at 12,
            // SizeOfRawData at 16, PointerToRawData at 20.
            _sectionTable = Add(optional, optionalSize);
            for (uint i = 0; i < sectionCount; i++)
            {
                uint s = Add(_sectionTable, i * 40);
                Need(s, 40);
                _sections.Add((U32(s + 12), U32(s + 8), U32(s + 20), U32(s + 16)));
            }
        }

        // ── Where things sit in the file, so a test can damage one and show
        //    that the damage is seen ──

        public uint DataDirectoryFileOffset(int index) => _directoryTable + (uint)index * 8;

        public uint SectionHeaderFileOffset(int index) => _sectionTable + (uint)index * 40;

        public uint ExportOrdinalTableFileOffset()
        {
            uint dir = Offset(_directories[ExportDirectory].Rva, 40);
            return Offset(U32(dir + 36), U32(dir + 24) * 2);
        }

        public uint ExportAddressTableFileOffset()
        {
            uint dir = Offset(_directories[ExportDirectory].Rva, 40);
            return Offset(U32(dir + 28), U32(dir + 20) * 4);
        }

        /// <summary>The file offset of the first import descriptor.</summary>
        public uint ImportTableFileOffset() => Offset(_directories[ImportDirectory].Rva, 20);

        /// <summary>The names the image exports. Each one is followed through
        /// its ordinal to an address inside a section, the way GetProcAddress
        /// resolves it, so a name that leads nowhere is an error and not an
        /// export.</summary>
        public IReadOnlyList<string> ExportedNames()
        {
            var (rva, size) = _directories[ExportDirectory];
            if (rva == 0 && size == 0) return Array.Empty<string>();
            if (size < 40) throw Bad("the export directory is shorter than its own header");

            uint dir = Offset(rva, 40);
            uint functionCount = U32(dir + 20);
            uint nameCount = U32(dir + 24);
            if (functionCount > 65536 || nameCount > 65536) throw Bad("an export count that cannot be real");

            var names = new List<string>();
            if (nameCount == 0) return names;

            uint functions = Offset(U32(dir + 28), functionCount * 4);
            uint nameTable = Offset(U32(dir + 32), nameCount * 4);
            uint ordinals = Offset(U32(dir + 36), nameCount * 2);
            for (uint i = 0; i < nameCount; i++)
            {
                ushort index = U16(ordinals + i * 2);
                if (index >= functionCount) throw Bad("export name " + i + " has an ordinal past the address table");
                uint address = U32(functions + (uint)index * 4);
                if (!InsideASection(address)) throw Bad("export name " + i + " has an address in no section");
                names.Add(AsciiAt(U32(nameTable + i * 4)));
            }
            return names;
        }

        /// <summary>The DLLs the image imports at load. The table ends at a
        /// descriptor of zeros, and that descriptor has to come before the
        /// directory's own size runs out, which is how both link.exe and lld
        /// write it.</summary>
        public IReadOnlyList<string> ImportedDlls()
        {
            var (rva, size) = _directories[ImportDirectory];
            var names = new List<string>();
            if (rva == 0 && size == 0) return names;

            // IMAGE_IMPORT_DESCRIPTOR is 20 bytes with the DLL name's RVA at 12.
            for (uint i = 0; ; i++)
            {
                if (((ulong)i + 1) * 20 > size) throw Bad("the import table does not end inside its directory");
                uint d = Offset(Add(rva, i * 20), 20);
                uint nameRva = U32(d + 12);
                if (nameRva == 0 && U32(d) == 0 && U32(d + 16) == 0) break;
                names.Add(AsciiAt(nameRva));
            }
            return names;
        }

        /// <summary>Whether the image has a delay-import table at all. Its
        /// DLLs load on first use, so they are companions too, and none of
        /// them shows in <see cref="ImportedDlls"/>.</summary>
        public bool HasDelayImports
            => _directories[DelayImportDirectory].Rva != 0 || _directories[DelayImportDirectory].Size != 0;

        private string AsciiAt(uint rva)
        {
            uint at = Offset(rva, 1);
            var text = new StringBuilder();
            while (true)
            {
                Need(at, 1);
                byte b = _image[at++];
                if (b == 0) return text.ToString();
                if (text.Length >= 512) throw Bad("a name with no end");
                text.Append((char)b);
            }
        }

        private bool InsideASection(uint rva)
        {
            if (rva == 0) return false;
            foreach (var s in _sections)
            {
                uint extent = Math.Max(s.VirtualSize, s.RawSize);
                if (rva >= s.Rva && (ulong)rva < (ulong)s.Rva + extent) return true;
            }
            return false;
        }

        /// <summary>The file offset of an RVA, which has to fall in the part
        /// of a section the file holds, for the whole length asked for.</summary>
        private uint Offset(uint rva, uint length)
        {
            foreach (var s in _sections)
            {
                if (rva < s.Rva) continue;
                uint into = rva - s.Rva;
                if (into >= s.RawSize) continue;
                if ((ulong)into + length > s.RawSize) throw Bad("RVA 0x" + rva.ToString("X") + " runs past its section's bytes");
                ulong at = (ulong)s.RawOffset + into;
                if (at + length > (ulong)_image.Length) throw Bad("RVA 0x" + rva.ToString("X") + " maps past the end of the file");
                return (uint)at;
            }
            throw Bad("RVA 0x" + rva.ToString("X") + " is in no section's bytes");
        }

        private static uint Add(uint a, uint b)
        {
            ulong sum = (ulong)a + b;
            if (sum > uint.MaxValue) throw Bad("an offset that wraps");
            return (uint)sum;
        }

        private ushort U16(uint at) { Need(at, 2); return BitConverter.ToUInt16(_image, (int)at); }
        private uint U32(uint at) { Need(at, 4); return BitConverter.ToUInt32(_image, (int)at); }

        private void Need(uint at, uint length)
        {
            if ((ulong)at + length > (ulong)_image.Length)
                throw Bad("offset 0x" + at.ToString("X") + " is past the end of the file");
        }

        private static InvalidDataException Bad(string what) => new InvalidDataException("Not a readable PE image: " + what);
    }
}
