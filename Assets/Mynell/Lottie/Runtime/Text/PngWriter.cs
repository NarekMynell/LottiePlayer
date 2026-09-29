using System.IO;
using System.IO.Compression;

namespace Mynell.Lottie.Text
{
    /// <summary>Minimal PNG encoder (8-bit RGBA, 'Sub' filtered rows) for glyph bitmaps rendered at compile time.</summary>
    static class PngWriter
    {
        static uint[] s_Crc;

        /// <summary>Encodes straight (not premultiplied) RGBA8 rows, top row first.</summary>
        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            int rowBytes = width * 4;
            var raw = new byte[(rowBytes + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int src = y * rowBytes, dst = y * (rowBytes + 1);
                raw[dst] = 1; // Sub
                for (int x = 0; x < rowBytes; x++)
                    raw[dst + 1 + x] = (byte)(rgba[src + x] - (x >= 4 ? rgba[src + x - 4] : 0));
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            BigEndian(ihdr, 0, (uint)width);
            BigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // RGBA
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", Zlib(raw));
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        static byte[] Zlib(byte[] data)
        {
            using var z = new MemoryStream();
            z.WriteByte(0x78);
            z.WriteByte(0x9C);
            using (var deflate = new DeflateStream(z, CompressionLevel.Optimal, true)) deflate.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            foreach (byte v in data)
            {
                a = (a + v) % 65521;
                b = (b + a) % 65521;
            }
            var adler = new byte[4];
            BigEndian(adler, 0, (b << 16) | a);
            z.Write(adler, 0, 4);
            return z.ToArray();
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var header = new byte[8];
            BigEndian(header, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
            s.Write(header, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc(0xFFFFFFFFu, header, 4, 4);
            crc = Crc(crc, data, 0, data.Length) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            BigEndian(tail, 0, crc);
            s.Write(tail, 0, 4);
        }

        static uint Crc(uint crc, byte[] data, int offset, int count)
        {
            if (s_Crc == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                s_Crc = table;
            }
            for (int i = 0; i < count; i++) crc = s_Crc[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static void BigEndian(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }
    }
}
