// Offline static decryptor for packed gakumas GameAssembly.dll
// Reconstructs the DllMain stub at file .text (ROL4/NOT string decode is unused here;
// header = fn 0xA0, bulk = fn 0x6E0, tail copy = fn 0x750).
//
// usage:
//   csc /nologo /out:ga_static_decrypt.exe ga_static_decrypt.cs
//   ga_static_decrypt.exe <packed.dll> <out.dll> [optional-initA-dump-to-compare]
using System;
using System.IO;
using System.Text;

internal static partial class Program
{
    const uint Konn = 0x4E4E4F4B; // 'KONN'
    const uint PayloadFileOff = 0x1000;

    static int Main(string[] args)
    {
        if (args.Length < 3 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("usage: ga-static-decrypt <packed.dll> <out.dll> <profileDir> [reference.dump]");
            return args.Length > 0 ? 0 : 2;
        }
        var packedPath = Path.GetFullPath(args[0]);
        var outPath = Path.GetFullPath(args[1]);
        var profileDir = Path.GetFullPath(args[2]);
        var dumpPath = args.Length >= 4 ? Path.GetFullPath(args[3]) : null;

        if (!File.Exists(packedPath))
        {
            Console.WriteLine("[err] packed input not found: " + packedPath);
            return 3;
        }
        if (!Directory.Exists(profileDir))
        {
            Console.WriteLine("[err] profile directory not found: " + profileDir);
            return 3;
        }
        if (dumpPath != null && !File.Exists(dumpPath))
        {
            Console.WriteLine("[err] reference dump not found: " + dumpPath);
            return 3;
        }

        try
        {
            var file = File.ReadAllBytes(packedPath);
            Console.WriteLine("[in] {0} ({1:N0} bytes)", packedPath, file.Length);

            var hdr = DecodeHeader(file, PayloadFileOff);
            if (hdr == null) return 3;
            PrintHeader(hdr);

            if (!TryParsePe(file, out var pe))
            {
                Console.WriteLine("[err] packed file is not a PE");
                return 3;
            }
            Console.WriteLine("[pe] ImageBase=0x{0:X} SizeOfImage=0x{1:X} entry=0x{2:X} secs={3}",
                pe.ImageBase, pe.SizeOfImage, pe.Entry, pe.Sections.Length);

            var image = new byte[pe.SizeOfImage];
            MapDiskSections(file, pe, image);

            int destRva = hdr[3];
            int srcOff = (int)PayloadFileOff + hdr[4];
            int chunk = hdr[6] - hdr[3] + 0x2000;
            uint key = (uint)hdr[0];
            Console.WriteLine("[dec] destRVA=0x{0:X} srcOff=0x{1:X} chunk=0x{2:X}({2:N0}) key=0x{3:X8}",
                destRva, srcOff, chunk, key);

            if (srcOff < 0 || destRva < 0 || chunk <= 0 ||
                srcOff + chunk > file.Length || destRva + chunk > image.Length)
            {
                Console.WriteLine("[err] header offsets out of range");
                return 4;
            }

            Decrypt6E0(image, destRva, file, srcOff, chunk, key);

            int tail = hdr[5] - chunk;
            if (tail > 0)
            {
                int tsrc = srcOff + chunk;
                int tdst = destRva + chunk;
                if (tsrc + tail > file.Length || tdst + tail > image.Length)
                    throw new InvalidDataException($"tail copy out of range: 0x{tail:X} bytes");
                Buffer.BlockCopy(file, tsrc, image, tdst, tail);
                Console.WriteLine("[dec] tail memcpy 0x{0:X} bytes -> RVA 0x{1:X}", tail, tdst);
            }

            ApplyStage2SelfDecrypt(image);
            int finalSize = FullBodyDecrypt.Apply(file, image, profileDir, dumpPath);
            WriteOutput(outPath, image, finalSize);
            Console.WriteLine("[out] {0} ({1:N0} bytes)", outPath, finalSize);

            DumpExports(image, Path.ChangeExtension(outPath, ".exports.txt"));
            SampleStrings(image, pe);
            if (dumpPath != null)
                CompareText(image, File.ReadAllBytes(dumpPath), pe);
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("[err] " + e.Message);
            return 1;
        }
    }

    static void WriteOutput(string path, byte[] image, int size)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var temp = path + ".tmp-" + Environment.ProcessId;
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                fs.Write(image, 0, size);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    static int[] DecodeHeader(byte[] file, uint off)
    {
        if (file.Length < off + 32)
        {
            Console.WriteLine("[err] file too small for header at 0x{0:X}", off);
            return null;
        }
        var src = new uint[8];
        var dst = new int[8];
        for (int i = 0; i < 8; i++)
            src[i] = BitConverter.ToUInt32(file, (int)off + i * 4);

        dst[0] = (int)src[0];
        uint acc = src[0];
        dst[1] = (int)(src[1] ^ acc);
        acc = acc + src[1];

        dst[2] = (int)(src[2] ^ acc);
        uint t = (src[2] + acc - 1) ^ 1;

        dst[3] = (int)(src[3] ^ t);
        t = (src[3] + t - 2) ^ 4;

        dst[4] = (int)(src[4] ^ t);
        t = (src[4] + t - 3) ^ 9;

        dst[5] = (int)(src[5] ^ t);
        t = (src[5] + t - 4) ^ 0x10;

        dst[6] = (int)(src[6] ^ t);
        t = (src[6] + t - 5) ^ 0x19;

        dst[7] = (int)(src[7] ^ t);

        Console.WriteLine("[hdr] raw  {0}", HexU32(src));
        Console.WriteLine("[hdr] dec  {0}", HexI32(dst));
        if ((uint)dst[1] != Konn)
        {
            Console.WriteLine("[err] magic mismatch: got 0x{0:X8} expected KONN (0x{1:X8})", (uint)dst[1], Konn);
            return null;
        }
        Console.WriteLine("[hdr] magic KONN ok");
        return dst;
    }

    static void PrintHeader(int[] h)
    {
        Console.WriteLine("[hdr] [0] key/flags = 0x{0:X8}", (uint)h[0]);
        Console.WriteLine("[hdr] [1] magic     = 0x{0:X8}", (uint)h[1]);
        Console.WriteLine("[hdr] [2]           = 0x{0:X8}", (uint)h[2]);
        Console.WriteLine("[hdr] [3] dest RVA  = 0x{0:X8}", (uint)h[3]);
        Console.WriteLine("[hdr] [4] src add   = 0x{0:X8}", (uint)h[4]);
        Console.WriteLine("[hdr] [5] total?    = 0x{0:X8} ({0:N0})", h[5]);
        Console.WriteLine("[hdr] [6] split     = 0x{0:X8}", (uint)h[6]);
        Console.WriteLine("[hdr] [7]           = 0x{0:X8}", (uint)h[7]);
    }

    // fn 0x6E0: dword stream, key' = key + ~size
    static void Decrypt6E0(byte[] dest, int destOff, byte[] src, int srcOff, int size, uint key)
    {
        int count = size >> 2;
        uint k = key + ~((uint)size);
        for (int i = 0; i < count; i++)
        {
            uint c = BitConverter.ToUInt32(src, srcOff + i * 4);
            uint p = c ^ k;
            k = k + (c + (uint)i);
            k ^= (uint)(i * i);
            dest[destOff + i * 4] = (byte)p;
            dest[destOff + i * 4 + 1] = (byte)(p >> 8);
            dest[destOff + i * 4 + 2] = (byte)(p >> 16);
            dest[destOff + i * 4 + 3] = (byte)(p >> 24);
        }
        int rem = size & 3;
        if (rem != 0)
            Buffer.BlockCopy(src, srcOff + count * 4, dest, destOff + count * 4, rem);
        Console.WriteLine("[dec] 0x6E0 wrote {0:N0} dwords ({1:N0} bytes)", count, count * 4);
    }

    struct Section
    {
        public string Name;
        public int VA, VSize, Raw, RSize;
        public uint Ch;
        public int TableOff;
    }

    struct Pe
    {
        public int Lfanew, OptStart, OptSize, Entry;
        public ulong ImageBase;
        public int SizeOfImage, SizeOfHeaders, NumRva;
        public Section[] Sections;
    }

    static bool TryParsePe(byte[] b, out Pe pe)
    {
        pe = default(Pe);
        if (b.Length < 0x40 || b[0] != 0x4D || b[1] != 0x5A) return false;
        int lfanew = BitConverter.ToInt32(b, 0x3C);
        if (lfanew < 0 || lfanew + 0x18 >= b.Length) return false;
        if (BitConverter.ToUInt32(b, lfanew) != 0x4550) return false;
        int nsec = BitConverter.ToUInt16(b, lfanew + 6);
        int optSize = BitConverter.ToUInt16(b, lfanew + 20);
        int opt = lfanew + 24;
        if (BitConverter.ToUInt16(b, opt) != 0x20B) return false;
        pe.Lfanew = lfanew;
        pe.OptStart = opt;
        pe.OptSize = optSize;
        pe.Entry = BitConverter.ToInt32(b, opt + 16);
        pe.ImageBase = BitConverter.ToUInt64(b, opt + 24);
        pe.SizeOfImage = BitConverter.ToInt32(b, opt + 56);
        pe.SizeOfHeaders = BitConverter.ToInt32(b, opt + 60);
        pe.NumRva = BitConverter.ToInt32(b, opt + 108);
        int secOff = opt + optSize;
        pe.Sections = new Section[nsec];
        for (int i = 0; i < nsec; i++)
        {
            int o = secOff + i * 40;
            pe.Sections[i] = new Section
            {
                Name = Encoding.ASCII.GetString(b, o, 8).TrimEnd('\0'),
                VSize = BitConverter.ToInt32(b, o + 8),
                VA = BitConverter.ToInt32(b, o + 12),
                RSize = BitConverter.ToInt32(b, o + 16),
                Raw = BitConverter.ToInt32(b, o + 20),
                Ch = BitConverter.ToUInt32(b, o + 36),
                TableOff = o,
            };
        }
        return true;
    }

    static void MapDiskSections(byte[] file, Pe pe, byte[] image)
    {
        int hdr = Math.Min(pe.SizeOfHeaders, Math.Min(file.Length, image.Length));
        Buffer.BlockCopy(file, 0, image, 0, hdr);
        foreach (var s in pe.Sections)
        {
            if (s.RSize <= 0 || s.Raw <= 0) continue;
            int n = Math.Min(s.RSize, Math.Min(s.VSize, file.Length - s.Raw));
            if (n <= 0 || s.VA + n > image.Length) continue;
            Buffer.BlockCopy(file, s.Raw, image, s.VA, n);
            Console.WriteLine("[map] {0,-8} file 0x{1:X} -> VA 0x{2:X} {3:N0}B", s.Name, s.Raw, s.VA, n);
        }
    }


    static void DumpExports(byte[] image, string path)
    {
        Pe pe;
        if (!TryParsePe(image, out pe)) return;
        int expRva = BitConverter.ToInt32(image, pe.OptStart + 112);
        int expSz = BitConverter.ToInt32(image, pe.OptStart + 116);
        if (expRva <= 0 || expRva + 40 > image.Length)
        {
            Console.WriteLine("[exp] no export dir");
            return;
        }
        int names = BitConverter.ToInt32(image, expRva + 24);
        int addrTbl = BitConverter.ToInt32(image, expRva + 28);
        int nameTbl = BitConverter.ToInt32(image, expRva + 32);
        int ordTbl = BitConverter.ToInt32(image, expRva + 36);
        var sb = new StringBuilder();
        int listed = 0;
        for (int i = 0; i < names && i < 4000; i++)
        {
            int nameRva = BitConverter.ToInt32(image, nameTbl + i * 4);
            ushort ord = BitConverter.ToUInt16(image, ordTbl + i * 2);
            int fnRva = BitConverter.ToInt32(image, addrTbl + ord * 4);
            string name = ReadZ(image, nameRva);
            sb.AppendLine(string.Format("{0}\t0x{1:x}", name, fnRva));
            listed++;
        }
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("[exp] {0} names -> {1}", listed, path);
        // show il2cpp_init
        foreach (var line in sb.ToString().Split('\n'))
        {
            if (line.StartsWith("il2cpp_init\t") || line.StartsWith("il2cpp_init\r"))
                Console.WriteLine("[exp] {0}", line.Trim());
        }
    }

    static void SampleStrings(byte[] image, Pe pe)
    {
        foreach (var s in pe.Sections)
        {
            if (s.Name != ".rdata") continue;
            int n = Math.Min(s.VSize, image.Length - s.VA);
            if (n <= 0) continue;
            var ascii = Encoding.ASCII.GetString(image, s.VA, Math.Min(n, 0x400000));
            foreach (var pat in new[] { "il2cpp_init", "il2cpp_runtime_invoke", "UnityEngine", "Assembly-CSharp", "Campus." })
            {
                int i = ascii.IndexOf(pat, StringComparison.Ordinal);
                Console.WriteLine("[str] {0} => {1}", pat, i >= 0 ? "0x" + (s.VA + i).ToString("X") : "NOT in first 4MB .rdata");
            }
        }
        // entropy of .text first 1MB
        foreach (var s in pe.Sections)
        {
            if (s.Name != ".text" && s.Name != "il2cpp") continue;
            int n = Math.Min(Math.Min(s.VSize, 1 << 20), image.Length - s.VA);
            if (n < 16) continue;
            Console.WriteLine("[ent] {0} first {1:N0}B entropy={2:F3}", s.Name, n, Entropy(image, s.VA, n));
        }
    }

    static void CompareText(byte[] image, byte[] dump, Pe pe)
    {
        Console.WriteLine("[cmp] initA dump {0:N0}B vs static {1:N0}B", dump.Length, image.Length);
        foreach (var s in pe.Sections)
        {
            if (s.Name != ".text" && s.Name != "il2cpp" && s.Name != ".pdata") continue;
            int n = Math.Min(s.VSize, Math.Min(image.Length, dump.Length) - s.VA);
            if (n <= 0) continue;
            int eq = 0;
            int first = -1;
            for (int i = 0; i < n; i++)
            {
                if (image[s.VA + i] == dump[s.VA + i]) eq++;
                else if (first < 0) first = i;
            }
            Console.WriteLine("[cmp] {0,-8} match {1:N0}/{2:N0} ({3:P2}) firstDiff=+0x{4:X}",
                s.Name, eq, n, (double)eq / n, first < 0 ? 0 : first);
        }
    }

    static double Entropy(byte[] b, int off, int n)
    {
        var h = new int[256];
        for (int i = 0; i < n; i++) h[b[off + i]]++;
        double e = 0, nn = n;
        for (int i = 0; i < 256; i++)
            if (h[i] > 0) { double p = h[i] / nn; e -= p * Math.Log(p, 2); }
        return e;
    }

    static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    static string ReadZ(byte[] b, int off)
    {
        if (off < 0 || off >= b.Length) return "";
        int e = off;
        while (e < b.Length && b[e] != 0) e++;
        return Encoding.ASCII.GetString(b, off, e - off);
    }


    static string HexU32(uint[] a)
    {
        var sb = new StringBuilder();
        foreach (var x in a) sb.Append(x.ToString("X8")).Append(' ');
        return sb.ToString();
    }

    static string HexI32(int[] a)
    {
        var sb = new StringBuilder();
        foreach (var x in a) sb.Append(((uint)x).ToString("X8")).Append(' ');
        return sb.ToString();
    }
}
