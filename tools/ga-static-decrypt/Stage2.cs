using System;

internal static partial class Program
{
    private static uint ReadU32(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }

    private static void DecryptRol(byte[] image, int destination, int size, uint seed, int rotation)
    {
        int count = size >> 2;
        uint state = seed;
        for (int i = 0; i < count; i++)
        {
            uint value = ReadU32(image, destination + i * 4) ^ state;
            state += (uint)i;
            value = Rol32(value, rotation) - (uint)i;
            WriteU32(image, destination + i * 4, value);
        }
        Console.WriteLine("[dec] ROL{0} RVA=0x{1:X} size=0x{2:X} seed=0x{3:X8} words={4}",
            rotation, destination, size, seed, count);
    }

    private static void DecryptRol3(byte[] image, int destination, int size, uint key)
    {
        DecryptRol3(image, destination, size, key, (byte)(key & 0xFF));
    }

    private static void DecryptRol3(byte[] image, int destination, int size, uint key, byte incomingR11)
    {
        byte r11 = (byte)((key >> 8) + incomingR11);
        byte dl = (byte)(r11 + 1);
        for (int i = 0; i < size && destination + i < image.Length; i++)
        {
            byte value = image[destination + i];
            value = Rol8(value, 3);
            value ^= dl++;
            value = Rol8(value, 3);
            value ^= r11++;
            image[destination + i] = Rol8(value, 3);
        }
        Console.WriteLine("[dec] ROL3 RVA=0x{0:X} size=0x{1:X} key=0x{2:X8} r11in=0x{3:X2}",
            destination, size, key, incomingR11);
    }

    private static void DecryptRol2(byte[] image, int destination, int size, uint key)
    {
        byte state = (byte)key;
        for (int i = 0; i < size && destination + i < image.Length; i++)
        {
            byte value = image[destination + i];
            byte next = (byte)(state + 1);
            value = Rol8(value, 2);
            value ^= next;
            value = Rol8(value, 2);
            value ^= state;
            state = next;
            image[destination + i] = Rol8(value, 2);
        }
        Console.WriteLine("[dec] ROL2 RVA=0x{0:X} size=0x{1:X} key=0x{2:X8}", destination, size, key);
    }

    private static byte Rol8(byte value, int rotation)
    {
        rotation &= 7;
        return (byte)((value << rotation) | (value >> (8 - rotation)));
    }

    private static uint Rol32(uint value, int rotation)
    {
        rotation &= 31;
        return (value << rotation) | (value >> (32 - rotation));
    }

    private static void ApplyStage2SelfDecrypt(byte[] image)
    {
        const int record11 = 0xBDE3868;
        int rva11 = (int)ReadU32(image, record11);
        int size11 = (int)ReadU32(image, record11 + 4);
        if (rva11 <= 0 || size11 < 8 || rva11 + size11 > image.Length)
            throw new InvalidOperationException($"ROL11 record invalid: RVA=0x{rva11:X}, size=0x{size11:X}");
        DecryptRol(image, rva11, size11, ReadU32(image, rva11), 11);

        const int record13 = 0xBDEAB10;
        int rva13 = (int)ReadU32(image, record13);
        int size13 = (int)ReadU32(image, record13 + 4);
        if (rva13 <= 0 || size13 < 8 || rva13 + size13 > image.Length)
            throw new InvalidOperationException($"ROL13 record invalid: RVA=0x{rva13:X}, size=0x{size13:X}");
        DecryptRol(image, rva13, size13, 0xB9C62858, 13);

        RequireBytes(image, 0xBDE9DB8, 0x48, 0x83, 0xEC, 0x28, "ROL13 helper");
        RequireBytes(image, 0xBDEDDE0, 0x48, 0x83, 0xEC, 0x28, "ROL3 helper");

        DecryptRol3(image, 0xBDF0A00, 0x7000, 0xBDF0A00);
        for (int i = 0; i < 17606; i++) image[0xBDF0E00 + i] ^= 0xFF;
        RequireBytes(image, 0xBDF4D70, 0x48, 0x83, 0xEC, 0x28, "stage-2 NOT helper");

        ApplyMapRecord(image, 0xBDEF7E0);
        ApplyMapRecord(image, 0xBDEF800);
        ApplyFlagTable(image, 0xBDEF900);
    }

    private static void ApplyMapRecord(byte[] image, int record)
    {
        uint flags = ReadU32(image, record);
        int destination = (int)ReadU32(image, record + 4);
        int size = (int)ReadU32(image, record + 8);
        int callOffset = (int)ReadU32(image, record + 16);
        if (destination <= 0 || size < 8 || destination + size > image.Length)
            throw new InvalidOperationException($"map record 0x{record:X} is out of range");
        if ((flags & 1) != 0) DecryptRol3(image, destination, size, (uint)destination);
        for (int i = 0; i < size - 0x400; i++) image[destination + 0x400 + i] ^= 0xFF;
        RequireBytes(image, destination + callOffset, 0x48, 0x83, 0xEC, 0x28, $"map helper 0x{record:X}");
    }

    private static void ApplyFlagTable(byte[] image, int table)
    {
        int count = (int)ReadU32(image, 0xBDEF790);
        if (count <= 0 || count > 8)
            throw new InvalidOperationException($"invalid stage-2 flag record count: {count}");

        for (int index = 0; index < count; index++)
        {
            int record = table + index * 16;
            uint flags = ReadU32(image, record);
            int destination = (int)ReadU32(image, record + 4);
            int size = (int)ReadU32(image, record + 8);
            if (destination <= 0 || size <= 0 || destination + size > image.Length)
                throw new InvalidOperationException($"flag record {index} is out of range");

            if ((flags & 1) != 0) DecryptRol3(image, destination, size, (uint)destination);
            if ((flags & 0x10) != 0) ApplyNestedPrefix(image, destination);
            if ((flags & 2) == 0) continue;

            int cursor = destination;
            for (int child = 0; child < 64 && cursor + 16 <= image.Length; child++, cursor += 16)
            {
                DecryptRol2(image, cursor, 16, (uint)cursor);
                uint source = ReadU32(image, cursor);
                uint length = ReadU32(image, cursor + 4);
                uint target = ReadU32(image, cursor + 8);
                uint length2 = ReadU32(image, cursor + 12);
                if (length == 0) break;
                if (length != length2 || target + length > (uint)image.Length || source + length > (uint)image.Length)
                    continue;

                bool populated = false;
                for (int i = 0; i < Math.Min(64, (int)length); i++)
                    populated |= image[(int)source + i] != 0;
                if (populated)
                    Buffer.BlockCopy(image, (int)source, image, (int)target, (int)length);
            }
        }

        DecryptRol3(image, 0xBE0BC00, 0x10A4, 0xBE0BC00, 0);
        DecryptRol3(image, 0xBE0CD98, 0xC3C, 0xBE0CD98, 0);
        DecryptRol3(image, 0xBE0CCA4, 0xF4, 0xBE0CCA4, 0);
        DecryptRol3(image, 0xBE0D9D4, 0xF4, 0xBE0D9D4, 0);
    }

    private static void ApplyNestedPrefix(byte[] image, int destination)
    {
        DecryptRol2(image, destination, 16, (uint)destination);
        int firstStart = (int)ReadU32(image, destination);
        int firstEnd = (int)ReadU32(image, destination + 4);
        int secondStart = (int)ReadU32(image, destination + 8);
        int secondEnd = (int)ReadU32(image, destination + 12);
        if (firstStart < 0 || firstEnd <= firstStart || destination + firstEnd > image.Length ||
            secondStart < 0 || secondEnd <= secondStart || destination + secondEnd > image.Length)
            throw new InvalidOperationException("nested stage-2 prefix is invalid");
        DecryptRol2(image, destination + firstStart, firstEnd - firstStart, (uint)(destination + firstStart));
        DecryptRol2(image, destination + secondStart, secondEnd - secondStart, (uint)(destination + secondStart));
    }

    private static void RequireBytes(byte[] image, int offset, byte a, byte b, byte c, byte d, string name)
    {
        if (offset < 0 || offset + 4 > image.Length || image[offset] != a || image[offset + 1] != b ||
            image[offset + 2] != c || image[offset + 3] != d)
            throw new InvalidOperationException($"{name} validation failed at RVA 0x{offset:X}");
    }
}
