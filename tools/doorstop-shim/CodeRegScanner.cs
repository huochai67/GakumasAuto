// Derives the Il2CppCodeRegistration constants for a decrypted gakumas image by
// scanning the image itself, so no per-version hardcoded RVAs are needed.
//
// Two structural facts are exploited (both cross-validated against the 2026-08 image,
// whose hand-derived constants are known: gmp 0x90B56E0/496142, cgm 0xA6A2140/185,
// and against the 2026-09 image: gmp 0x90ED490/497076, cgm 0xA6E1500/185):
//
//  1. Il2CppCodeRegistration.genericMethodPointers is a huge contiguous array of
//     pointers into the *generated code* section ("il2cpp", not ".text"). It shows
//     up as the longest maximal run of qwords that point into an executable section.
//     Entry 0 is a null placeholder, so the array starts one slot before the first
//     non-null element and its count is runLength + 1.
//
//  2. Il2CppCodeRegistration.codeGenModules is an array of pointers to
//     Il2CppCodeGenModule structs, whose field 0 is a `const char*` module name
//     ("Assembly-CSharp.dll", ...), field +8 a method count and field +16 the method
//     pointer table. Locating name strings -> module structs -> the (contiguous)
//     array of module pointers recovers the array address and its length
//     (= number of assemblies).
//
// The decrypted image stores absolute virtual addresses (imageBase + RVA), so every
// qword is normalized to an RVA before comparison; RVAs are what gets cached and
// returned, matching tools/interop-gen.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace GakumasDoorstopShim
{
    internal sealed class ImageConstants
    {
        internal ulong ImageBase;
        internal ulong ImageSize;
        internal ulong GenericMethodPointersRva;
        internal ulong GenericMethodPointersCount;
        internal ulong AddrCodeGenModulePtrsRva;
        internal ulong CodeGenModulesCount;
        internal string Source = "scan";

        internal string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "imageBase=0x{0:X} imageSize=0x{1:X} gmp=0x{2:X}({3}) cgm=0x{4:X}({5}) src={6}",
                ImageBase, ImageSize, GenericMethodPointersRva, GenericMethodPointersCount,
                AddrCodeGenModulePtrsRva, CodeGenModulesCount, Source);
        }
    }

    internal static class CodeRegScanner
    {
        private const int MaxModuleNameLength = 96;
        private const int MaxArrayGapSlots = 3;
        private const int MinGenericMethodPointers = 50000;
        private const int MinCodeGenModules = 32;
        private const int MaxCodeGenModules = 4096;

        private struct Section
        {
            internal string Name;
            internal uint Rva;
            internal uint VSize;
            internal uint RawPtr;
            internal uint RawSize;
            internal bool Executable;

            internal ulong RvaEnd => (ulong)Rva + VSize;
            internal bool ContainsRva(ulong rva) => rva >= Rva && rva < RvaEnd;
            internal long RvaToOffset(ulong rva) => (long)RawPtr + (long)(rva - Rva);
        }

        // ------------------------------------------------------------------ cache

        internal static ImageConstants GetOrScan(string imagePath, string cachePath)
        {
            string key = null;
            if (!string.IsNullOrEmpty(cachePath))
            {
                try { key = CacheKey(imagePath); } catch { key = null; }
                if (key != null)
                {
                    var cached = TryReadCache(cachePath, key);
                    if (cached != null)
                    {
                        ShimLog.Write("[scan] cache hit (" + cachePath + ")");
                        return cached;
                    }
                }
            }

            var result = Scan(imagePath);
            result.Source = "scan";

            if (!string.IsNullOrEmpty(cachePath) && key != null)
            {
                try { WriteCache(cachePath, key, imagePath, result); }
                catch (Exception e) { ShimLog.Write("[scan] cache write failed: " + e.Message); }
            }
            return result;
        }

        private static string CacheKey(string imagePath)
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists) throw new FileNotFoundException("image not found: " + imagePath, imagePath);
            ulong head = Fnv1A(imagePath, 0, 1 << 20);
            ulong tail = Fnv1A(imagePath, Math.Max(0, fi.Length - (1 << 20)), 1 << 20);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:X16}:{3:X16}",
                fi.Length, fi.LastWriteTimeUtc.Ticks, head, tail);
        }

        private static ulong Fnv1A(string path, long offset, int length)
        {
            using var fs = File.OpenRead(path);
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(length, 1 << 20)];
            int read = fs.Read(buf, 0, buf.Length);
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < read; i++)
            {
                h ^= buf[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        private static ImageConstants TryReadCache(string cachePath, string key)
        {
            try
            {
                if (!File.Exists(cachePath)) return null;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(cachePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                if (!values.TryGetValue("key", out var k) || k != key) return null;
                return new ImageConstants
                {
                    ImageBase = ParseHex(values["imageBase"]),
                    ImageSize = ParseHex(values["imageSize"]),
                    GenericMethodPointersRva = ParseHex(values["gmpRva"]),
                    GenericMethodPointersCount = ulong.Parse(values["gmpCount"], CultureInfo.InvariantCulture),
                    AddrCodeGenModulePtrsRva = ParseHex(values["cgrRva"]),
                    CodeGenModulesCount = ulong.Parse(values["cgmCount"], CultureInfo.InvariantCulture),
                    Source = "cache",
                };
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(string cachePath, string key, string imagePath, ImageConstants c)
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine("# GakumasDoorstopShim derived Il2CppCodeRegistration constants");
            sb.AppendLine("key=" + key);
            sb.AppendLine("image=" + imagePath);
            sb.AppendLine("imageBase=0x" + c.ImageBase.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine("imageSize=0x" + c.ImageSize.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine("gmpRva=0x" + c.GenericMethodPointersRva.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine("gmpCount=" + c.GenericMethodPointersCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("cgrRva=0x" + c.AddrCodeGenModulePtrsRva.ToString("X", CultureInfo.InvariantCulture));
            sb.AppendLine("cgmCount=" + c.CodeGenModulesCount.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(cachePath, sb.ToString(), Encoding.UTF8);
        }

        private static ulong ParseHex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------- scan

        internal static ImageConstants Scan(string imagePath)
        {
            using var mmf = MemoryMappedFile.CreateFromFile(imagePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            unsafe
            {
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                try
                {
                    long length = view.Capacity;
                    var sections = ReadSections(p, length, out ulong imageBase, out ulong imageSize);
                    var result = new ImageConstants { ImageBase = imageBase, ImageSize = imageSize };

                    ScanGenericMethodPointers(p, length, sections, imageBase, imageSize, result);
                    ScanCodeGenModules(p, length, sections, imageBase, imageSize, result);
                    return result;
                }
                finally
                {
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        private static unsafe List<Section> ReadSections(byte* p, long length, out ulong imageBase, out ulong imageSize)
        {
            imageBase = 0;
            imageSize = 0;
            if (length < 0x1000) throw new InvalidDataException("image too small to be a PE file");
            if (*(ushort*)p != 0x5A4D) throw new InvalidDataException("no MZ header (not a PE image)");

            int peOffset = *(int*)(p + 0x3C);
            if (peOffset <= 0 || peOffset + 0x100 > length) throw new InvalidDataException("bad e_lfanew");
            if (*(uint*)(p + peOffset) != 0x00004550) throw new InvalidDataException("no PE signature");

            int sectionCount = *(ushort*)(p + peOffset + 6);
            int optionalHeaderSize = *(ushort*)(p + peOffset + 20);
            imageBase = *(ulong*)(p + peOffset + 24 + 24);

            long sectionOffset = peOffset + 24 + optionalHeaderSize;
            var sections = new List<Section>(sectionCount);
            for (int i = 0; i < sectionCount; i++)
            {
                long o = sectionOffset + i * 40;
                if (o + 40 > length) throw new InvalidDataException("section header out of range");
                var nameBytes = new byte[8];
                for (int b = 0; b < 8; b++) nameBytes[b] = p[o + b];
                int nameLen = Array.IndexOf(nameBytes, (byte)0);
                if (nameLen < 0) nameLen = 8;

                sections.Add(new Section
                {
                    Name = Encoding.ASCII.GetString(nameBytes, 0, nameLen),
                    VSize = *(uint*)(p + o + 8),
                    Rva = *(uint*)(p + o + 12),
                    RawSize = *(uint*)(p + o + 16),
                    RawPtr = *(uint*)(p + o + 20),
                    Executable = (*(uint*)(p + o + 36) & 0x20000000) != 0, // IMAGE_SCN_MEM_EXECUTE
                });
            }

            if (sections.Count == 0) throw new InvalidDataException("no sections");
            foreach (var s in sections)
            {
                var end = s.RvaEnd;
                if (end > imageSize) imageSize = end;
            }
            return sections;
        }

        private static long RvaToOffset(List<Section> sections, ulong rva)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (!s.ContainsRva(rva)) continue;
                long offset = s.RvaToOffset(rva);
                return offset >= 0 ? offset : -1;
            }
            return -1;
        }

        private static ulong OffsetToRva(List<Section> sections, long offset)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (s.RawSize == 0) continue;
                long start = s.RawPtr;
                long end = start + s.RawSize;
                if (offset >= start && offset < end)
                    return (ulong)s.Rva + (ulong)(offset - start);
            }
            return 0;
        }

        private static bool IsExecutableRva(List<Section> sections, ulong rva)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (s.Executable && s.ContainsRva(rva)) return true;
            }
            return false;
        }

        /// <summary>Decrypted images store imageBase + RVA; accept plain RVAs too.</summary>
        private static ulong Normalize(ulong value, ulong imageBase, ulong imageSize)
        {
            if (value >= imageBase)
            {
                var rva = value - imageBase;
                if (rva < imageSize) return rva;
            }
            return value;
        }

        private static unsafe void ScanGenericMethodPointers(byte* p, long length, List<Section> sections, ulong imageBase, ulong imageSize, ImageConstants result)
        {
            long slots = length / 8;
            long bestStart = -1, bestLength = 0;
            long currentStart = 0, currentLength = 0;

            for (long i = 0; i < slots; i++)
            {
                ulong value = *(ulong*)(p + i * 8);
                if (value != 0 && IsExecutableRva(sections, Normalize(value, imageBase, imageSize)))
                {
                    if (currentLength == 0) currentStart = i;
                    currentLength++;
                    if (currentLength > bestLength)
                    {
                        bestLength = currentLength;
                        bestStart = currentStart;
                    }
                }
                else
                {
                    currentLength = 0;
                }
            }

            if (bestStart < 0 || bestLength < MinGenericMethodPointers)
                throw new InvalidDataException("genericMethodPointers run not found (best run " + bestLength + " qwords); image is probably still packed - set BEPINEX_GAME_ASSEMBLY_PATH to the decrypted image");

            long arrayStart = bestStart;
            ulong count = (ulong)bestLength;

            // entry 0 of the array is a null placeholder
            if (bestStart > 0 && *(ulong*)(p + (bestStart - 1) * 8) == 0 && OffsetToRva(sections, (bestStart - 1) * 8) != 0)
            {
                arrayStart = bestStart - 1;
                count += 1;
            }

            ulong arrayRva = OffsetToRva(sections, arrayStart * 8);
            if (arrayRva == 0) throw new InvalidDataException("genericMethodPointers array is not inside a section");

            result.GenericMethodPointersRva = arrayRva;
            result.GenericMethodPointersCount = count;
        }

        private static unsafe void ScanCodeGenModules(byte* p, long length, List<Section> sections, ulong imageBase, ulong imageSize, ImageConstants result)
        {
            // (1) module name strings: NUL-terminated "<name>.dll"
            var nameStrings = new HashSet<ulong>();
            for (long offset = 0; offset < length - 1; offset++)
            {
                if (!IsNameChar(p[offset], first: true)) continue;
                long end = offset;
                while (end < length && IsNameChar(p[end], first: false)) end++;
                long nameLength = end - offset;
                if (nameLength < 5 || nameLength > MaxModuleNameLength || end >= length || p[end] != 0 || !EndsWithDll(p, offset, end))
                {
                    offset = end;
                    continue;
                }
                ulong rva = OffsetToRva(sections, offset);
                if (rva != 0) nameStrings.Add(rva);
                offset = end;
            }

            if (nameStrings.Count < MinCodeGenModules)
                throw new InvalidDataException("only " + nameStrings.Count + " module name strings found; image is probably still packed");

            // (2) Il2CppCodeGenModule structs: field 0 is a module name pointer
            long slots = length / 8;
            var moduleStructs = new HashSet<ulong>();
            for (long i = 0; i < slots; i++)
            {
                ulong value = *(ulong*)(p + i * 8);
                if (!nameStrings.Contains(Normalize(value, imageBase, imageSize))) continue;

                ulong structRva = OffsetToRva(sections, i * 8);
                if (structRva == 0 || (structRva & 7) != 0) continue;

                long structOffset = RvaToOffset(sections, structRva);
                if (structOffset < 0 || structOffset + 24 > length) continue;

                uint methodPointerCount = *(uint*)(p + structOffset + 8);
                ulong methodPointers = *(ulong*)(p + structOffset + 16);
                if (methodPointerCount > 5_000_000) continue;
                if (methodPointerCount != 0 && methodPointers == 0) continue;
                if (methodPointers != 0 && Normalize(methodPointers, imageBase, imageSize) >= imageSize) continue;

                moduleStructs.Add(structRva);
            }

            if (moduleStructs.Count < MinCodeGenModules)
                throw new InvalidDataException("only " + moduleStructs.Count + " codegen module structs found");

            // (3) the array of module pointers, allowing a few unmatched slots
            var slotIndices = new List<long>(moduleStructs.Count);
            for (long i = 0; i < slots; i++)
            {
                if (moduleStructs.Contains(Normalize(*(ulong*)(p + i * 8), imageBase, imageSize))) slotIndices.Add(i);
            }
            if (slotIndices.Count < MinCodeGenModules)
                throw new InvalidDataException("only " + slotIndices.Count + " module pointer slots found");

            long runFirst = slotIndices[0], runLast = slotIndices[0];
            long bestFirst = runFirst, bestLast = runLast, bestFilled = 1;
            long filled = 1;
            for (int i = 1; i < slotIndices.Count; i++)
            {
                long index = slotIndices[i];
                if (index - runLast <= MaxArrayGapSlots)
                {
                    runLast = index;
                    filled++;
                }
                else
                {
                    if (filled > bestFilled) { bestFilled = filled; bestFirst = runFirst; bestLast = runLast; }
                    runFirst = runLast = index;
                    filled = 1;
                }
            }
            if (filled > bestFilled) { bestFilled = filled; bestFirst = runFirst; bestLast = runLast; }

            long span = bestLast - bestFirst + 1;
            if (span < MinCodeGenModules || span > MaxCodeGenModules)
                throw new InvalidDataException("implausible codegen module array length " + span);
            if (bestFilled * 4 < span * 3)
                throw new InvalidDataException("codegen module array is only " + bestFilled + "/" + span + " filled");

            ulong arrayRva = OffsetToRva(sections, bestFirst * 8);
            if (arrayRva == 0) throw new InvalidDataException("codegen module array is not inside a section");

            result.AddrCodeGenModulePtrsRva = arrayRva;
            result.CodeGenModulesCount = (ulong)span;
        }

        private static unsafe bool EndsWithDll(byte* p, long start, long end)
        {
            if (end - start < 5) return false;
            long e = end;
            return p[e - 4] == (byte)'.' && Lower(p[e - 3]) == 'd' && Lower(p[e - 2]) == 'l' &&
                   Lower(p[e - 1]) == 'l';
        }

        private static byte Lower(byte b) => (byte)(b >= 'A' && b <= 'Z' ? b + 32 : b);

        private static bool IsNameChar(byte b, bool first)
        {
            bool alnum = (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
            if (first) return alnum || b == '_';
            return alnum || b == '_' || b == '.' || b == '-' || b == '+';
        }
    }
}
