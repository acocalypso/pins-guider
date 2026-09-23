// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using PinsGuider.Engine.Core;

namespace PinsGuider.Plugin.Hardware;

/// <summary>
/// Minimal in-memory FITS reader for guide frames delivered as INDI BLOBs (no temp files, no native code).
/// Supports the primary HDU with BITPIX 8, 16 (with BZERO 32768) and 32/-32 (scaled to 16 bit), NAXIS 2
/// (or NAXIS 3 with a single plane), and zlib-compressed BLOBs (".fits.z").
/// </summary>
internal static class FitsReader
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public static GuideFrame Read(byte[] data, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (format is not null && format.EndsWith(".z", StringComparison.OrdinalIgnoreCase))
        {
            data = Decompress(data);
        }

        int offset = 0;
        return ReadHdu(data, ref offset, out _) ?? throw new InvalidDataException("FITS primary HDU has no image");
    }

    /// <summary>Reads every image HDU (primary and IMAGE extensions) with its header keywords.</summary>
    public static IReadOnlyList<(GuideFrame Frame, IReadOnlyDictionary<string, string> Header)> ReadAll(byte[] data)
    {
        var result = new List<(GuideFrame, IReadOnlyDictionary<string, string>)>();
        int offset = 0;
        while (offset + BlockSize <= data.Length)
        {
            var frame = ReadHdu(data, ref offset, out var header);
            if (frame is not null)
            {
                result.Add((frame, header));
            }
        }

        return result;
    }

    /// <summary>Writes frames as a multi-HDU 16-bit FITS file (primary + IMAGE extensions) with extra keywords per HDU.</summary>
    public static void Write(Stream stream, IReadOnlyList<(GuideFrame Frame, IReadOnlyDictionary<string, string> Keywords)> hdus)
    {
        for (int h = 0; h < hdus.Count; h++)
        {
            var (f, kw) = hdus[h];
            var cards = new List<string>
            {
                h == 0 ? Card("SIMPLE", "T") : Card("XTENSION", "'IMAGE   '"),
                Card("BITPIX", "16"),
                Card("NAXIS", "2"),
                Card("NAXIS1", f.Width.ToString(CultureInfo.InvariantCulture)),
                Card("NAXIS2", f.Height.ToString(CultureInfo.InvariantCulture)),
            };
            if (h > 0)
            {
                cards.Add(Card("PCOUNT", "0"));
                cards.Add(Card("GCOUNT", "1"));
            }
            else
            {
                cards.Add(Card("EXTEND", "T"));
            }

            cards.Add(Card("BZERO", "32768"));
            cards.Add(Card("BSCALE", "1"));
            foreach (var (k, v) in kw)
            {
                cards.Add(Card(k, v));
            }

            cards.Add("END".PadRight(CardSize));
            var header = string.Concat(cards);
            header = header.PadRight((header.Length + BlockSize - 1) / BlockSize * BlockSize);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(header));

            int n = f.Width * f.Height;
            int padded = (n * 2 + BlockSize - 1) / BlockSize * BlockSize;
            var buf = new byte[padded];
            for (int i = 0; i < n; i++)
            {
                BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(i * 2, 2), (short)(f.Pixels[i] - 32768));
            }

            stream.Write(buf);
        }
    }

    private static string Card(string key, string value) =>
        (value.StartsWith('\'') ? $"{key,-8}= {value}" : $"{key,-8}= {value,20}").PadRight(CardSize);

    private static GuideFrame? ReadHdu(byte[] data, ref int offset, out IReadOnlyDictionary<string, string> headerOut)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headerOut = header;
        bool end = false;
        while (!end)
        {
            if (offset + BlockSize > data.Length)
            {
                throw new InvalidDataException("FITS header truncated");
            }

            for (int c = 0; c < BlockSize / CardSize; c++)
            {
                string card = System.Text.Encoding.ASCII.GetString(data, offset + c * CardSize, CardSize);
                string key = card[..8].Trim();
                if (key == "END")
                {
                    end = true;
                    break;
                }

                if (card.Length > 9 && card[8] == '=')
                {
                    string value = card[10..];
                    int slash = value.IndexOf('/');
                    if (slash >= 0 && !value.TrimStart().StartsWith('\''))
                    {
                        value = value[..slash];
                    }

                    header[key] = value.Trim().Trim('\'').Trim();
                }
            }

            offset += BlockSize;
        }

        int bitpix = Int(header, "BITPIX");
        int naxis = Int(header, "NAXIS");
        long dataBytes = 0;
        if (naxis > 0)
        {
            dataBytes = Math.Abs(bitpix) / 8;
            for (int a = 1; a <= naxis; a++)
            {
                dataBytes *= Int(header, "NAXIS" + a.ToString(CultureInfo.InvariantCulture));
            }
        }

        int dataStart = offset;
        offset += (int)((dataBytes + BlockSize - 1) / BlockSize * BlockSize);
        if (naxis < 2)
        {
            return null;
        }

        int width = Int(header, "NAXIS1");
        int height = Int(header, "NAXIS2");
        double bzero = Dbl(header, "BZERO", 0);
        double bscale = Dbl(header, "BSCALE", 1);
        int n = checked(width * height);
        int bytesPer = Math.Abs(bitpix) / 8;
        if (dataStart + (long)n * bytesPer > data.Length)
        {
            throw new InvalidDataException("FITS data truncated");
        }

        var pixels = new ushort[n];
        var src = data.AsSpan(dataStart);
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < n; i++)
                {
                    pixels[i] = (ushort)Math.Clamp(src[i] * bscale + bzero, 0, 65535);
                }

                break;
            case 16:
                if (bscale == 1 && bzero == 32768)
                {
                    for (int i = 0; i < n; i++)
                    {
                        pixels[i] = (ushort)(BinaryPrimitives.ReadInt16BigEndian(src.Slice(i * 2, 2)) + 32768);
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        pixels[i] = (ushort)Math.Clamp(BinaryPrimitives.ReadInt16BigEndian(src.Slice(i * 2, 2)) * bscale + bzero, 0, 65535);
                    }
                }

                break;
            case 32:
                for (int i = 0; i < n; i++)
                {
                    pixels[i] = (ushort)Math.Clamp(BinaryPrimitives.ReadInt32BigEndian(src.Slice(i * 4, 4)) * bscale + bzero, 0, 65535);
                }

                break;
            case -32:
                for (int i = 0; i < n; i++)
                {
                    float f = BinaryPrimitives.ReadSingleBigEndian(src.Slice(i * 4, 4));
                    double v = f * bscale + bzero;
                    pixels[i] = (ushort)Math.Clamp(v <= 1.0 ? v * 65535.0 : v, 0, 65535);
                }

                break;
            default:
                throw new InvalidDataException($"FITS BITPIX={bitpix} not supported");
        }

        return new GuideFrame(width, height, pixels) { BitsPerPixel = bitpix == 8 ? 8 : 16 };
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 3);
        z.CopyTo(output);
        return output.ToArray();
    }

    private static int Int(Dictionary<string, string> h, string key) =>
        h.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw new InvalidDataException($"FITS keyword {key} missing");

    private static double Dbl(Dictionary<string, string> h, string key, double def) =>
        h.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
}
