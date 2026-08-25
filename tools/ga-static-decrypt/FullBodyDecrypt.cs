using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class FullBodyDecrypt
{
    private const int RecordTableOffset = 0x2E0;
    private const int PackedPayloadOffset = 0xE3580;
    private const int FinalImageSize = 0x0BD26000;
    private const int ExpectedRecordCount = 47706;
    private const int MemCommit = 0x1000;
    private const int MemReserve = 0x2000;
    private const int MemRelease = 0x8000;
    private const int PageExecuteReadWrite = 0x40;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HelperDelegate(IntPtr key, uint rounds, IntPtr input, IntPtr output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, int allocationType, int protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, nuint size, int freeType);

    internal static int Apply(byte[] packed, byte[] image, string profileDirectory, string referencePath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("the validated body helper uses Windows x64 machine code");
        if (image.Length < FinalImageSize)
            throw new InvalidDataException($"intermediate image is too small: {image.Length:N0} bytes");

        byte[] helperTable = LoadProfile(profileDirectory, "helper_tab.bin", 20480,
            "574fff3681c9f2e63416c9bf93714946a212a31c9854ebea3653de09e8013f30");
        byte[] helperCode = LoadProfile(profileDirectory, "helper_fn.bin", 1536,
            "597d522dc44d8568a52891355bc72d2ef0a54bffa9124f227be53063d42e5326");
        byte[] key = LoadProfile(profileDirectory, "ga_clean_key_pid13208.bin", 240,
            "9750f95959d9c002e4338f0eeb14e0ce7c3edf4c2caca11b97c0fa31674f7517");
        byte[] decodeTable = LoadProfile(profileDirectory, "ga_pass3tab_from_initA.bin", 4260,
            "a70105c5c6902dac22cb8d99b912797273b63210e500e45cd906c5dac0259dbb");
        byte[] stage2 = LoadProfile(profileDirectory, "ga_guard_s2_pid19232.bin", 929792,
            "a12249da42b17989ff49af481f84e7620cb357bfe5f82b94ba01d827dd4e9481");
        byte[] reference = referencePath == null ? null : File.ReadAllBytes(referencePath);

        IntPtr executable = VirtualAlloc(IntPtr.Zero, 0x8000, MemCommit | MemReserve, PageExecuteReadWrite);
        if (executable == IntPtr.Zero)
            throw new InvalidOperationException("VirtualAlloc for validated body helper failed: " + Marshal.GetLastWin32Error());

        var keyHandle = default(GCHandle);
        var cipherHandle = default(GCHandle);
        var intermediateHandle = default(GCHandle);
        try
        {
            Marshal.Copy(helperTable, 0, executable, helperTable.Length);
            Marshal.Copy(helperCode, 0, IntPtr.Add(executable, 0x16C4), helperCode.Length);
            var helper = Marshal.GetDelegateForFunctionPointer<HelperDelegate>(IntPtr.Add(executable, 0x16C4));

            byte[] cipher = new byte[0x1100];
            byte[] intermediate = new byte[0x1100];
            keyHandle = GCHandle.Alloc(key, GCHandleType.Pinned);
            cipherHandle = GCHandle.Alloc(cipher, GCHandleType.Pinned);
            intermediateHandle = GCHandle.Alloc(intermediate, GCHandleType.Pinned);

            int fileOffset = PackedPayloadOffset;
            int records = 0;
            int validStreams = 0;
            int invalidStreams = 0;
            long comparedBytes = 0;
            long matchedBytes = 0;

            for (int recordOffset = RecordTableOffset; recordOffset + 16 <= stage2.Length; recordOffset += 16)
            {
                uint destination = ReadU32(stage2, recordOffset);
                uint packedSize = ReadU32(stage2, recordOffset + 4);
                uint destination2 = ReadU32(stage2, recordOffset + 8);
                uint unpackedSize = ReadU32(stage2, recordOffset + 12);
                if (destination != destination2 || destination < 0x1000 || destination + unpackedSize > image.Length ||
                    packedSize == 0 || packedSize > 0x1000 || unpackedSize == 0 || unpackedSize > 0x1000)
                    break;

                int storedSize = checked((int)((packedSize + 3) & ~3u));
                int fullBlocksSize = checked((int)(packedSize & ~15u));
                int decodeSpan = (storedSize + 15) & ~15;
                if (fileOffset + storedSize > packed.Length)
                    throw new InvalidDataException($"record {records} exceeds packed input at 0x{fileOffset:X}");

                Array.Clear(cipher);
                Array.Clear(intermediate);
                Buffer.BlockCopy(packed, fileOffset, cipher, 0, storedSize);
                Buffer.BlockCopy(cipher, 0, intermediate, 0, storedSize);

                IntPtr keyPointer = keyHandle.AddrOfPinnedObject();
                IntPtr cipherPointer = cipherHandle.AddrOfPinnedObject();
                IntPtr intermediatePointer = intermediateHandle.AddrOfPinnedObject();
                for (int offset = 0; offset < fullBlocksSize; offset += 16)
                    helper(keyPointer, 14, IntPtr.Add(cipherPointer, offset), IntPtr.Add(intermediatePointer, offset));
                for (int offset = 16; offset < fullBlocksSize; offset++)
                    intermediate[offset] ^= cipher[offset - 16];
                for (int offset = 0; offset < packedSize; offset++)
                    intermediate[offset] = Substitute(intermediate[offset]);

                int bits;
                if (packedSize == unpackedSize)
                {
                    Buffer.BlockCopy(intermediate, 0, image, checked((int)destination), checked((int)unpackedSize));
                    bits = checked((int)packedSize * 8);
                }
                else
                {
                    bits = Decode(intermediate, decodeSpan, decodeTable, image,
                        checked((int)destination), checked((int)unpackedSize));
                }

                if (bits >= 0 && (bits + 7) / 8 == packedSize)
                    validStreams++;
                else
                    invalidStreams++;

                if (reference != null && destination + unpackedSize <= reference.Length)
                {
                    for (int offset = 0; offset < unpackedSize; offset++)
                    {
                        if (image[destination + offset] == reference[destination + offset]) matchedBytes++;
                        comparedBytes++;
                    }
                }

                fileOffset += storedSize;
                records++;
            }

            Console.WriteLine("[body] records={0} streams={1}/{2}", records, validStreams, invalidStreams);
            if (reference != null)
                Console.WriteLine("[body] reference match={0:N0}/{1:N0} ({2:P6})",
                    matchedBytes, comparedBytes, comparedBytes == 0 ? 0 : (double)matchedBytes / comparedBytes);
            if (records != ExpectedRecordCount)
                throw new InvalidDataException($"body profile mismatch: expected {ExpectedRecordCount} records, decoded {records}");
            if (invalidStreams != 0 || validStreams != records)
                throw new InvalidDataException($"body stream invariant failed: valid={validStreams}, invalid={invalidStreams}");

            RebuildOriginalPe(image);
            return FinalImageSize;
        }
        finally
        {
            if (intermediateHandle.IsAllocated) intermediateHandle.Free();
            if (cipherHandle.IsAllocated) cipherHandle.Free();
            if (keyHandle.IsAllocated) keyHandle.Free();
            VirtualFree(executable, 0, MemRelease);
        }
    }

    private static byte[] LoadProfile(string directory, string name, int expectedLength, string expectedSha256)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException("required decrypt profile file is missing", path);
        byte[] data = File.ReadAllBytes(path);
        if (data.Length != expectedLength)
            throw new InvalidDataException($"profile file {name} has size {data.Length}, expected {expectedLength}");
        string actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"profile file {name} SHA-256 mismatch: {actual}");
        return data;
    }

    private static byte Substitute(byte value)
    {
        value = (byte)(value + 1);
        value ^= 0xB0;
        value = (byte)((value >> 1) | (value << 7));
        return (byte)(value + 0x0E);
    }

    private static uint Peek32(byte[] source, int sourceLength, int bitOffset)
    {
        int byteOffset = bitOffset >> 3;
        ulong value = 0;
        for (int i = 0; i < 4 && byteOffset + i < sourceLength; i++)
            value |= (ulong)source[byteOffset + i] << (8 * i);
        return (uint)(value >> (bitOffset & 7));
    }

    private static int Decode(byte[] source, int sourceLength, byte[] table, byte[] destination,
        int start, int length)
    {
        int bitOffset = 0;
        int output = start;
        int end = start + length;
        int hold = 0;
        while (output < end)
        {
            uint bits = Peek32(source, sourceLength, bitOffset);
            int index = (int)(bits & 0xFF);
            int entry = index * 3;
            if (entry + 3 > table.Length) return -output;
            ushort node = (ushort)(table[entry] | table[entry + 1] << 8);
            byte bitLength = table[entry + 2];
            int symbol;
            int lengthBits;
            if ((node & 0x8000) != 0)
            {
                symbol = node & 0x7FFF;
                lengthBits = bitLength;
            }
            else
            {
                int consumed = bitLength + 1;
                uint mask = 1u << (consumed - 1);
                index = (node & 0x7FFF) + ((bits & mask) != 0 ? 1 : 0);
                int guard;
                for (guard = 0; guard < 32; guard++)
                {
                    entry = index * 3;
                    if (entry + 3 > table.Length) return -output;
                    node = (ushort)(table[entry] | table[entry + 1] << 8);
                    bitLength = table[entry + 2];
                    if ((node & 0x8000) != 0) break;
                    mask <<= 1;
                    consumed++;
                    index = (node & 0x7FFF) + ((bits & mask) != 0 ? 1 : 0);
                }
                if ((node & 0x8000) == 0) return -output;
                symbol = node & 0x7FFF;
                lengthBits = bitLength;
            }

            bitOffset += lengthBits;
            int flags = symbol & 0x300;
            int payload = symbol & 0xFF;
            if (flags == 0)
            {
                destination[output++] = (byte)payload;
            }
            else if (flags == 0x100)
            {
                hold = hold == 0 ? payload : (hold << 8) | payload;
            }
            else if (flags == 0x300)
            {
                int distance = hold + payload;
                if (distance <= 0 || payload > end - output || output < distance) return -output;
                for (int i = 0; i < payload; i++)
                {
                    destination[output] = destination[output - distance];
                    output++;
                }
                hold = 0;
            }
            else if (flags == 0x200)
            {
                int repetitions = hold == 0 ? 1 : hold;
                int unit = payload;
                int count = repetitions * unit;
                if (count > end - output) return -output;
                if ((unit == 1 || unit == 2 || unit == 4) && output >= unit)
                    for (int i = 0; i < count; i++)
                        destination[output + i] = destination[output - unit + i % unit];
                output += count;
                hold = 0;
            }
            else
            {
                destination[output++] = (byte)payload;
            }
        }
        return bitOffset;
    }

    private static void RebuildOriginalPe(byte[] image)
    {
        string[] names = { ".text", "il2cpp", ".rdata", ".data", ".pdata", ".reloc" };
        uint[] virtualSizes = { 0x987330, 0x791DE8C, 0x233396C, 0xCC2FB0, 0x6DE108, 0x3A8678 };
        uint[] virtualAddresses = { 0x1000, 0x989000, 0x82A7000, 0xA5DB000, 0xB29E000, 0xB97D000 };
        uint[] characteristics = { 0x60000020, 0x60000020, 0x40000040, 0xC0000040, 0x40000040, 0x42000040 };

        if (ReadU16(image, 0) != 0x5A4D)
            throw new InvalidDataException("decoded image is not an MZ executable");
        int pe = checked((int)ReadU32(image, 0x3C));
        if (pe < 0 || pe + 0x108 > image.Length || ReadU32(image, pe) != 0x4550)
            throw new InvalidDataException("decoded image has an invalid PE header");
        int optional = pe + 24;
        int sectionTable = optional + ReadU16(image, pe + 20);
        if (sectionTable + names.Length * 40 > image.Length)
            throw new InvalidDataException("decoded section table is out of range");

        WriteU16(image, pe + 6, 6);
        WriteU32(image, optional + 8, 0x378F600);
        WriteU32(image, optional + 56, FinalImageSize);
        WriteU32(image, optional + 112 + 8, 0);
        WriteU32(image, optional + 112 + 12, 0);
        WriteU32(image, optional + 112 + 40, 0xB97D000);
        WriteU32(image, optional + 112 + 44, 0x3A8678);

        for (int i = 0; i < names.Length; i++)
        {
            int section = sectionTable + i * 40;
            Array.Clear(image, section, 40);
            for (int j = 0; j < names[i].Length; j++) image[section + j] = (byte)names[i][j];
            uint next = i + 1 < names.Length ? virtualAddresses[i + 1] : FinalImageSize;
            WriteU32(image, section + 8, virtualSizes[i]);
            WriteU32(image, section + 12, virtualAddresses[i]);
            WriteU32(image, section + 16, next - virtualAddresses[i]);
            WriteU32(image, section + 20, virtualAddresses[i]);
            WriteU32(image, section + 36, characteristics[i]);
        }

        uint debugRva = ReadU32(image, optional + 112 + 48);
        uint debugSize = ReadU32(image, optional + 112 + 52);
        if (debugRva + debugSize <= image.Length)
            for (int offset = 0; offset + 28 <= debugSize; offset += 28)
                WriteU32(image, checked((int)debugRva) + offset + 24,
                    ReadU32(image, checked((int)debugRva) + offset + 20));
    }

    private static ushort ReadU16(byte[] data, int offset)
    {
        return (ushort)(data[offset] | data[offset + 1] << 8);
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
    }

    private static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
