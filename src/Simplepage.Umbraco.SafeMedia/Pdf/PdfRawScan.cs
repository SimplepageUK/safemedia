using System.IO.Compression;
using System.Text;

namespace Simplepage.Umbraco.SafeMedia.Pdf;

/// <summary>
/// The exhaustive view of a PDF: every byte of the file, ignoring its cross-reference index.
///
/// A PDF reader normally finds objects through the index, and so does PdfPig. But objects can
/// exist outside it - leftovers from incremental saves, content after %%EOF, an object stream
/// the index never mentions - and some readers find and use them when repairing a file. This
/// walks the whole file with a small tokeniser that knows only what it needs to: names (with
/// #xx escapes decoded), strings (skipped, except where they are link addresses), dictionaries,
/// "N G obj" headers, and streams. Every object stream is decompressed and scanned too, so an
/// object hidden in an unlisted compressed stream is still seen.
///
/// Other streams - page content, images, fonts - are skipped without being decoded. They cannot
/// hold actions or dictionaries a reader would act on, and scanning page content would turn
/// every resource name into a false positive.
///
/// It is cruder than PdfPig on purpose, and it can only ever add refusals: nothing it finds
/// makes a file acceptable.
/// </summary>
internal static class PdfRawScan
{
    /// <summary>Total decompressed bytes across all object streams before the file is refused.</summary>
    internal const long MaxDecodedBytes = 64L * 1024 * 1024;

    internal sealed class Findings
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public HashSet<int> ObjectNumbers { get; } = new();

        /// <summary>Strings that are values of a URI or Base key: link addresses.</summary>
        public List<string> LinkAddresses { get; } = new();

        public List<string> Problems { get; } = new();

        public long DecodedBytes { get; set; }
    }

    public static Findings Scan(byte[] data)
    {
        var findings = new Findings();
        ScanBytes(data, findings, insideObjectStream: false);
        return findings;
    }

    /// <summary>What a dictionary has said so far, enough to know whether a stream after it is an object stream.</summary>
    private sealed class Dict
    {
        public bool ExpectKey = true;
        public string? Key;
        public int ArrayDepth;
        public bool IsObjectStream;
        public int? First;
        public bool DecodeParms;
        public List<string> Filters { get; } = new();
    }

    private static void ScanBytes(byte[] d, Findings findings, bool insideObjectStream)
    {
        var stack = new Stack<Dict>();
        Dict? lastClosed = null;
        long? previousInt = null, lastInt = null;
        var i = 0;

        while (i < d.Length)
        {
            var c = d[i];

            if (IsWhite(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case (byte)'%':
                    while (i < d.Length && d[i] != '\n' && d[i] != '\r') i++;
                    continue;

                case (byte)'(':
                {
                    var (text, next) = ReadLiteralString(d, i);
                    OnString(stack, findings, text);
                    i = next;
                    continue;
                }

                case (byte)'<' when i + 1 < d.Length && d[i + 1] == '<':
                    stack.Push(new Dict());
                    i += 2;
                    continue;

                case (byte)'<':
                {
                    var (text, next) = ReadHexString(d, i);
                    OnString(stack, findings, text);
                    i = next;
                    continue;
                }

                case (byte)'>' when i + 1 < d.Length && d[i + 1] == '>':
                    if (stack.Count > 0)
                    {
                        var closed = stack.Pop();
                        if (stack.Count == 0)
                        {
                            lastClosed = closed;
                        }
                        else
                        {
                            ValueDone(stack.Peek());
                        }
                    }
                    i += 2;
                    continue;

                case (byte)'[':
                    if (stack.Count > 0) stack.Peek().ArrayDepth++;
                    i++;
                    continue;

                case (byte)']':
                    if (stack.Count > 0 && stack.Peek().ArrayDepth > 0 && --stack.Peek().ArrayDepth == 0)
                    {
                        ValueDone(stack.Peek());
                    }
                    i++;
                    continue;

                case (byte)'/':
                {
                    var (name, next) = ReadName(d, i + 1);
                    findings.Names.Add(name);
                    OnName(stack, name);
                    previousInt = lastInt = null;
                    i = next;
                    continue;
                }
            }

            if (IsNumberStart(c))
            {
                var start = i;
                while (i < d.Length && IsRegular(d[i])) i++;
                var token = Encoding.ASCII.GetString(d, start, i - start);

                if (long.TryParse(token, out var value))
                {
                    previousInt = lastInt;
                    lastInt = value;
                    OnInteger(stack, value);
                }
                else
                {
                    previousInt = lastInt = null;
                    if (stack.Count > 0 && stack.Peek().ArrayDepth == 0) ValueDone(stack.Peek());
                }
                continue;
            }

            if (IsRegular(c))
            {
                var start = i;
                while (i < d.Length && IsRegular(d[i])) i++;
                var keyword = Encoding.ASCII.GetString(d, start, i - start);

                switch (keyword)
                {
                    case "obj":
                        if (previousInt is { } number && lastInt is not null && number is >= 0 and <= int.MaxValue)
                        {
                            findings.ObjectNumbers.Add((int)number);
                        }
                        stack.Clear();
                        lastClosed = null;
                        break;

                    case "R":
                        // "12 0 R" is a reference, not a literal: a First or Length given this way is unknown.
                        if (stack.Count > 0 && stack.Peek().Key == "First") stack.Peek().First = null;
                        if (stack.Count > 0 && stack.Peek().ArrayDepth == 0) ValueDone(stack.Peek());
                        break;

                    case "stream":
                        i = HandleStream(d, i, lastClosed, findings, insideObjectStream);
                        lastClosed = null;
                        break;

                    default:
                        if (stack.Count > 0 && stack.Peek().ArrayDepth == 0) ValueDone(stack.Peek());
                        break;
                }

                previousInt = lastInt = null;
                continue;
            }

            i++;
        }
    }

    private static void OnName(Stack<Dict> stack, string name)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var dict = stack.Peek();

        if (dict.ArrayDepth == 0 && dict.ExpectKey)
        {
            dict.Key = name;
            dict.ExpectKey = false;

            if (name == "DecodeParms")
            {
                dict.DecodeParms = true;
            }

            return;
        }

        // A name as a value.
        if (dict.Key == "Type" && name == "ObjStm")
        {
            dict.IsObjectStream = true;
        }

        // Only the full key: /F is a file specification in actions, not a filter, outside inline images.
        if (dict.Key == "Filter")
        {
            dict.Filters.Add(name);
        }

        if (dict.ArrayDepth == 0)
        {
            ValueDone(dict);
        }
    }

    private static void OnInteger(Stack<Dict> stack, long value)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var dict = stack.Peek();

        if (dict.ArrayDepth == 0 && !dict.ExpectKey)
        {
            if (dict.Key == "First" && value is >= 0 and <= int.MaxValue)
            {
                dict.First = (int)value;
            }

            // The key itself is kept, so a following "0 R" can mark the value as a reference.
            ValueDone(dict);
        }
    }

    private static void OnString(Stack<Dict> stack, Findings findings, string text)
    {
        if (stack.Count == 0)
        {
            return;
        }

        var dict = stack.Peek();

        if (dict.Key is "URI" or "Base")
        {
            findings.LinkAddresses.Add(text);
        }

        if (dict.ArrayDepth == 0)
        {
            ValueDone(dict);
        }
    }

    private static void ValueDone(Dict dict)
    {
        dict.ExpectKey = true;
    }

    private static int HandleStream(byte[] d, int i, Dict? dict, Findings findings, bool insideObjectStream)
    {
        var start = i;
        if (start < d.Length && d[start] == '\r') start++;
        if (start < d.Length && d[start] == '\n') start++;

        // Skip to the FIRST "endstream", ignoring /Length. Readers disagree about which to trust,
        // and stopping at the earliest one means anything a length-ignoring reader would see as
        // top-level objects is tokenised here too.
        var end = IndexOf(d, "endstream"u8, start);
        if (end < 0)
        {
            end = d.Length;
        }

        if (dict is { IsObjectStream: true })
        {
            if (insideObjectStream)
            {
                findings.Problems.Add("contains an object stream nested inside another, which is not valid PDF");
            }
            else
            {
                ScanObjectStream(d.AsSpan(start, end - start).ToArray(), dict, findings);
            }
        }

        return end;
    }

    private static void ScanObjectStream(byte[] raw, Dict dict, Findings findings)
    {
        if (dict.DecodeParms)
        {
            findings.Problems.Add("contains compressed objects using settings this check does not support, so they cannot be checked");
            return;
        }

        if (dict.First is not { } first)
        {
            findings.Problems.Add("contains compressed objects whose layout cannot be read, so they cannot be checked");
            return;
        }

        var decoded = raw;

        foreach (var filter in dict.Filters)
        {
            decoded = filter switch
            {
                "FlateDecode" or "Fl" => Inflate(decoded, findings),
                "ASCIIHexDecode" or "AHx" => AsciiHex(decoded),
                "ASCII85Decode" or "A85" => Ascii85(decoded),
                _ => null,
            } ?? Array.Empty<byte>();

            if (decoded.Length == 0)
            {
                findings.Problems.Add($"contains compressed objects that cannot be decompressed ({filter}), so they cannot be checked");
                return;
            }
        }

        findings.DecodedBytes += decoded.Length;
        if (findings.DecodedBytes > MaxDecodedBytes)
        {
            findings.Problems.Add("expands to more than 64 MB when decompressed");
            return;
        }

        // The header is pairs of "objectNumber offset" up to /First.
        var header = Encoding.ASCII.GetString(decoded, 0, Math.Min(first, decoded.Length));
        var parts = header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var k = 0; k + 1 < parts.Length; k += 2)
        {
            if (int.TryParse(parts[k], out var number) && number >= 0)
            {
                findings.ObjectNumbers.Add(number);
            }
        }

        ScanBytes(decoded, findings, insideObjectStream: true);
    }

    private static byte[]? Inflate(byte[] data, Findings findings)
    {
        // Most writers use zlib framing; a few write raw deflate. Try both.
        return TryDecompress(data, raw: false, findings) ?? TryDecompress(data, raw: true, findings);
    }

    private static byte[]? TryDecompress(byte[] data, bool raw, Findings findings)
    {
        try
        {
            using var input = new MemoryStream(data);
            using Stream inflater = raw
                ? new DeflateStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            var buffer = new byte[81920];
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (findings.DecodedBytes + output.Length > MaxDecodedBytes)
                {
                    return output.ToArray(); // the caller's size check refuses it
                }
            }

            return output.Length > 0 ? output.ToArray() : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        int? high = null;

        foreach (var b in data)
        {
            if (b == '>') break;
            var v = HexValue(b);
            if (v < 0) continue;
            if (high is null) { high = v; } else { output.Add((byte)((high.Value << 4) | v)); high = null; }
        }

        if (high is not null) output.Add((byte)(high.Value << 4));
        return output.ToArray();
    }

    private static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var group = new int[5];
        var count = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '~') break;
            if (IsWhite(b)) continue;
            if (b == 'z' && count == 0) { output.AddRange(new byte[4]); continue; }
            if (b < '!' || b > 'u') return Array.Empty<byte>();

            group[count++] = b - '!';
            if (count == 5)
            {
                Emit(group, 4, output);
                count = 0;
            }
        }

        if (count > 1)
        {
            for (var k = count; k < 5; k++) group[k] = 84;
            Emit(group, count - 1, output);
        }

        return output.ToArray();

        static void Emit(int[] g, int bytes, List<byte> output)
        {
            long value = 0;
            foreach (var digit in g) value = (value * 85) + digit;
            for (var k = 0; k < bytes; k++) output.Add((byte)(value >> (24 - (8 * k))));
        }
    }

    private static (string Name, int Next) ReadName(byte[] d, int i)
    {
        var bytes = new List<byte>();

        while (i < d.Length && IsRegular(d[i]))
        {
            if (d[i] == '#' && i + 2 < d.Length && HexValue(d[i + 1]) >= 0 && HexValue(d[i + 2]) >= 0)
            {
                bytes.Add((byte)((HexValue(d[i + 1]) << 4) | HexValue(d[i + 2])));
                i += 3;
            }
            else
            {
                bytes.Add(d[i++]);
            }
        }

        return (Encoding.Latin1.GetString(bytes.ToArray()), i);
    }

    private static (string Text, int Next) ReadLiteralString(byte[] d, int i)
    {
        var sb = new StringBuilder();
        var depth = 0;

        for (; i < d.Length; i++)
        {
            var b = d[i];

            if (b == '\\' && i + 1 < d.Length)
            {
                var e = d[++i];
                switch (e)
                {
                    case (byte)'n': sb.Append('\n'); break;
                    case (byte)'r': sb.Append('\r'); break;
                    case (byte)'t': sb.Append('\t'); break;
                    case (byte)'b': sb.Append('\b'); break;
                    case (byte)'f': sb.Append('\f'); break;
                    case (byte)'\r': if (i + 1 < d.Length && d[i + 1] == '\n') i++; break;
                    case (byte)'\n': break;
                    case >= (byte)'0' and <= (byte)'7':
                        var octal = e - '0';
                        for (var k = 0; k < 2 && i + 1 < d.Length && d[i + 1] >= '0' && d[i + 1] <= '7'; k++)
                        {
                            octal = (octal * 8) + (d[++i] - '0');
                        }
                        sb.Append((char)(octal & 0xFF));
                        break;
                    default: sb.Append((char)e); break;
                }
                continue;
            }

            if (b == '(')
            {
                if (depth++ > 0) sb.Append('(');
                continue;
            }

            if (b == ')')
            {
                if (--depth == 0) return (sb.ToString(), i + 1);
                sb.Append(')');
                continue;
            }

            sb.Append((char)b);
        }

        return (sb.ToString(), i);
    }

    private static (string Text, int Next) ReadHexString(byte[] d, int i)
    {
        var end = Array.IndexOf(d, (byte)'>', i + 1);
        if (end < 0) end = d.Length;
        var decoded = AsciiHex(d.AsSpan(i + 1, end - i - 1).ToArray());
        return (Encoding.Latin1.GetString(decoded), Math.Min(end + 1, d.Length));
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsRegular(byte b) => !IsWhite(b) && !IsDelimiter(b);

    private static bool IsNumberStart(byte b) => b is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.';

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int from)
    {
        var found = haystack.AsSpan(from).IndexOf(needle);
        return found < 0 ? -1 : from + found;
    }
}
