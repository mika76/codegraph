using System.Text;

namespace CodeGraph.Text;

/// <summary>Reading source files and mapping offsets to line numbers.</summary>
public static class SourceText
{
    static SourceText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Read a source file. Tries UTF-16 (BOM), then strict UTF-8 (with/without BOM), then Windows-1252.</summary>
    public static string Read(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] raw)
    {
        if (raw.Length >= 2 && (raw[0] == 0xFF && raw[1] == 0xFE || raw[0] == 0xFE && raw[1] == 0xFF))
        {
            try
            {
                var enc = raw[0] == 0xFF ? new UnicodeEncoding(false, true, true) : new UnicodeEncoding(true, true, true);
                return enc.GetString(raw, 2, raw.Length - 2);
            }
            catch (DecoderFallbackException) { }
        }
        int start = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, true).GetString(raw, start, raw.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(raw);
        }
    }

    public static int CountLines(string text)
    {
        int n = 1;
        foreach (char c in text)
            if (c == '\n') n++;
        return n;
    }
}

/// <summary>Map character offsets to 1-based line numbers.</summary>
public sealed class LineIndex
{
    private readonly List<int> _starts = [0];

    public LineIndex(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') _starts.Add(i + 1);
    }

    public int Line(int pos)
    {
        int i = _starts.BinarySearch(pos);
        // BinarySearch returns the index of an exact match, or the complement of the next larger element
        return i >= 0 ? i + 1 : ~i;
    }
}
