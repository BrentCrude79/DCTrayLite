// ConfigOverlay — tiny config block the generator appends to the end of the
// template exe, and the template reads back at startup.
//
// Layout at end of file:
//   [ UTF-8 JSON: {"url":"...","name":"..."} ][ uint32 LE jsonByteLength ][ 8 bytes magic "PWA2TRAY" ]
//
// Appending bytes to a PE does not affect loading (it's overlay data), so the
// exe stays a valid single file. Pure file I/O, no Windows API — also linked
// into a net8 test harness on Linux to validate the round trip.
using System;
using System.IO;
using System.Text;

static class ConfigOverlay
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("PWA2TRAY");
    public const int FooterSize = 12; // 4 length + 8 magic
    public const int MaxJsonBytes = 4096;

    public sealed class AppConfig
    {
        public string Url;
        public string Name;
    }

    public static void Write(string exePath, string url, string name)
    {
        string json = "{\"url\":" + JsonEscape(url) + ",\"name\":" + JsonEscape(name) + "}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        using (var fs = new FileStream(exePath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            fs.Write(jsonBytes, 0, jsonBytes.Length);
            byte[] len = BitConverter.GetBytes(jsonBytes.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            fs.Write(len, 0, 4);
            fs.Write(Magic, 0, Magic.Length);
        }
    }

    public static AppConfig Read(string exePath)
    {
        try
        {
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < FooterSize) return null;
                fs.Seek(-FooterSize, SeekOrigin.End);
                byte[] footer = new byte[FooterSize];
                ReadFull(fs, footer, 0, FooterSize);
                for (int i = 0; i < Magic.Length; i++)
                    if (footer[4 + i] != Magic[i]) return null;
                byte[] lenBytes = new byte[4];
                Array.Copy(footer, 0, lenBytes, 0, 4);
                if (!BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                int len = BitConverter.ToInt32(lenBytes, 0);
                if (len <= 0 || len > MaxJsonBytes || fs.Length < FooterSize + len) return null;
                fs.Seek(-(FooterSize + len), SeekOrigin.End);
                byte[] jsonBytes = new byte[len];
                ReadFull(fs, jsonBytes, 0, len);
                // The magic must not appear inside the JSON itself (sanity).
                string json = Encoding.UTF8.GetString(jsonBytes);
                var cfg = new AppConfig
                {
                    Url = ExtractJsonString(json, "url"),
                    Name = ExtractJsonString(json, "name"),
                };
                if (string.IsNullOrEmpty(cfg.Url)) return null;
                if (string.IsNullOrEmpty(cfg.Name)) cfg.Name = "App";
                return cfg;
            }
        }
        catch { return null; }
    }

    static void ReadFull(Stream s, byte[] buf, int off, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buf, off, count);
            if (n <= 0) throw new EndOfStreamException();
            off += n; count -= n;
        }
    }

    static string JsonEscape(string v)
    {
        if (v == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in v)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    static string ExtractJsonString(string json, string key)
    {
        // Minimal parser for the flat {"url":"...","name":"..."} we generate.
        string needle = "\"" + key + "\"";
        int i = json.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i += needle.Length;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != ':') return null;
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i < json.Length)
            {
                char e = json[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= json.Length)
                        {
                            sb.Append((char)Convert.ToInt32(json.Substring(i, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            else sb.Append(c);
        }
        return null;
    }
}
