using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FocusTime.Api.Modules.Identity;

internal static class LoginCaptchaImage
{
    private const int Width = 144;
    private const int Height = 48;
    private const int HeaderSize = 54;
    private const string Alphabet = "23456789ACDEFGHJKLMNPQRTUVWXY";

    private static ReadOnlySpan<byte> Glyphs =>
    [
        14,17,1,2,4,8,31,    // 2
        30,1,1,14,1,1,30,   // 3
        2,6,10,18,31,2,2,    // 4
        31,16,16,30,1,1,30, // 5
        14,16,16,30,17,17,14, // 6
        31,1,2,4,8,8,8,     // 7
        14,17,17,14,17,17,14, // 8
        14,17,17,15,1,1,14, // 9
        14,17,17,31,17,17,17, // A
        14,17,16,16,16,17,14, // C
        30,17,17,17,17,17,30, // D
        31,16,16,30,16,16,31, // E
        31,16,16,30,16,16,16, // F
        14,17,16,23,17,17,14, // G
        17,17,17,31,17,17,17, // H
        7,2,2,2,2,18,12,    // J
        17,18,20,24,20,18,17, // K
        16,16,16,16,16,16,31, // L
        17,27,21,21,17,17,17, // M
        17,25,21,19,17,17,17, // N
        30,17,17,30,16,16,16, // P
        14,17,17,17,21,18,13, // Q
        30,17,17,30,20,18,17, // R
        31,4,4,4,4,4,4,      // T
        17,17,17,17,17,17,14, // U
        17,17,17,17,17,10,4, // V
        17,17,17,21,21,27,17, // W
        17,17,10,4,10,17,17, // X
        17,17,10,4,4,4,4     // Y
    ];

    internal static byte[] Create(out string answer)
    {
        Span<char> letters = stackalloc char[5];
        var bitmap = new byte[HeaderSize + Width * Height * 3];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2), bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(18), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(22), -Height);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bitmap.AsSpan(28), 24);
        bitmap.AsSpan(HeaderSize).Fill(245);

        var random = Random.Shared;
        for (var dot = 0; dot < 520; dot++)
            Pixel(bitmap, random.Next(Width), random.Next(Height), (byte)random.Next(179, 235), 186, 166);

        for (var index = 0; index < letters.Length; index++)
        {
            var glyph = RandomNumberGenerator.GetInt32(Alphabet.Length);
            letters[index] = Alphabet[glyph];
            DrawGlyph(bitmap, Glyphs.Slice(glyph * 7, 7), 17 + index * 27 + random.Next(-2, 3),
                24 + random.Next(-4, 5), random.NextDouble() * 0.56 - 0.28, random.NextDouble() * 0.5 + 3);
        }

        for (var line = 0; line < 2; line++)
        {
            var phase = random.NextDouble() * Math.Tau;
            for (var x = 0; x < Width; x++)
                Pixel(bitmap, x, 15 + line * 13 + (int)(7 * Math.Sin(x / 16.0 + phase)), 185, 164, 150);
        }
        answer = new string(letters);
        return bitmap;
    }

    private static void DrawGlyph(byte[] bitmap, ReadOnlySpan<byte> glyph, int centerX, int centerY, double angle, double scale)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        for (var y = -18; y <= 18; y++)
        for (var x = -15; x <= 15; x++)
        {
            var column = (int)Math.Floor((cosine * x + sine * y) / scale + 2.5);
            var row = (int)Math.Floor((-sine * x + cosine * y) / scale + 3.5);
            if (column is >= 0 and < 5 && row is >= 0 and < 7 && (glyph[row] & (16 >> column)) != 0)
                Pixel(bitmap, centerX + x, centerY + y, 98, 45, 49);
        }
    }

    private static void Pixel(byte[] bitmap, int x, int y, byte blue, byte green, byte red)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        var offset = HeaderSize + (y * Width + x) * 3;
        bitmap[offset] = blue;
        bitmap[offset + 1] = green;
        bitmap[offset + 2] = red;
    }
}
