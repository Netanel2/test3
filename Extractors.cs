using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace Madrich
{
    public class Section
    {
        public string Source { get; set; }
        public int? Page { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// חילוץ תוכן ממדריכים. Word/תמונות/טקסט — זירו תלות מלא.
    /// PDF — חילוץ בסיסי (ראה הערה למטה).
    /// </summary>
    public static class Extractors
    {
        public const string VisionPrompt =
@"אתה עוזר שמתעד מדריכי הפעלה של מערכות מחשב בעברית.
מולך צילום מסך מתוך מדריך. תאר אותו בעברית בצורה מדויקת ומעשית, כך שמישהו שקורא את התיאור (בלי לראות את התמונה) יוכל לדעת בדיוק מה לעשות:

1. תמלל את *כל* הטקסט הגלוי בתמונה (תפריטים, כפתורים, כותרות, שדות, הודעות).
2. תאר את מיקום הרכיבים ('כפתור כחול בפינה הימנית העליונה', 'תפריט צד שמאלי').
3. הסבר איזו פעולה התמונה מדגימה — על מה לוחצים, מה ממלאים, מה קורה אחרי.
4. אם רואים שם של מערכת/מסך/חלון — ציין אותו.

אל תמציא. אם משהו לא ברור, כתוב 'לא ברור מהתמונה'. כתוב בעברית בלבד.";

        public static async Task<List<Section>> ExtractAsync(AiClient ai, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".docx": return await FromDocxAsync(ai, path);
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".webp":
                case ".bmp":
                case ".gif": return await FromImageAsync(ai, path);
                case ".txt":
                case ".md": return FromText(path);
                case ".pdf": return await FromPdfAsync(ai, path);
                default: return new List<Section>();
            }
        }

        // ---------- טקסט פשוט ----------
        private static List<Section> FromText(string path)
        {
            var txt = File.ReadAllText(path, Encoding.UTF8);
            return new List<Section>
            {
                new Section { Source = Path.GetFileName(path), Page = null, Text = txt }
            };
        }

        // ---------- תמונה ----------
        private static async Task<List<Section>> FromImageAsync(AiClient ai, string path)
        {
            string fname = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            string media = MediaTypeFor(Path.GetExtension(path));
            string desc = await ai.DescribeImageAsync(bytes, media, VisionPrompt +
                          "\n\nהקשר: " + fname);
            return new List<Section>
            {
                new Section { Source = fname, Page = null, Text = desc }
            };
        }

        // ---------- Word (.docx) — ZIP + XML מובנה ----------
        private static async Task<List<Section>> FromDocxAsync(AiClient ai, string path)
        {
            string fname = Path.GetFileName(path);
            var sections = new List<Section>();

            using var zip = ZipFile.OpenRead(path);

            // 1) הטקסט של המסמך
            var docEntry = zip.GetEntry("word/document.xml");
            if (docEntry != null)
            {
                using var s = docEntry.Open();
                string text = ExtractDocxText(s);
                if (!string.IsNullOrWhiteSpace(text))
                    sections.Add(new Section { Source = fname, Page = null, Text = text });
            }

            // 2) תמונות מוטמעות (word/media/*)
            int imgIdx = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
                {
                    string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" &&
                        ext != ".gif" && ext != ".bmp") continue;

                    imgIdx++;
                    byte[] bytes;
                    using (var es = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        es.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    string media = MediaTypeFor(ext);
                    string desc = await ai.DescribeImageAsync(bytes, media,
                        VisionPrompt + "\n\nהקשר: " + fname + " · תמונה " + imgIdx);
                    sections.Add(new Section
                    {
                        Source = fname, Page = null,
                        Text = "[צילום מסך " + imgIdx + "]\n" + desc
                    });
                }
            }
            return sections;
        }

        private static string ExtractDocxText(Stream xmlStream)
        {
            var sb = new StringBuilder();
            var settings = new XmlReaderSettings { IgnoreWhitespace = false };
            using var reader = XmlReader.Create(xmlStream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    // <w:t> = טקסט,  <w:p> = סוף פסקה,  <w:br> = שבירת שורה
                    if (reader.LocalName == "t")
                    {
                        sb.Append(reader.ReadElementContentAsString());
                    }
                    else if (reader.LocalName == "p")
                    {
                        sb.Append('\n');
                    }
                    else if (reader.LocalName == "br" || reader.LocalName == "cr")
                    {
                        sb.Append('\n');
                    }
                }
            }
            return sb.ToString().Trim();
        }

        // ---------- PDF — חילוץ בסיסי (best-effort) ----------
        // הערה: זה עובד ל-PDF טקסטואליים פשוטים. ל-PDF עם צילומי מסך/פונטים
        // מיוחדים — עדיף לייצא את העמודים ל-PNG ולזרוק אותם לתיקייה.
        private static Task<List<Section>> FromPdfAsync(AiClient ai, string path)
        {
            string fname = Path.GetFileName(path);
            var sections = new List<Section>();
            try
            {
                byte[] data = File.ReadAllBytes(path);
                string text = PdfBasicText.Extract(data);
                if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length > 20)
                {
                    sections.Add(new Section { Source = fname, Page = null, Text = text });
                }
                else
                {
                    sections.Add(new Section
                    {
                        Source = fname, Page = null,
                        Text = "[לא הצלחתי לחלץ טקסט מקובץ ה-PDF הזה. " +
                               "כנראה מדובר ב-PDF תמונתי/סרוק. " +
                               "ייצא את העמודים ל-PNG וזרוק אותם לתיקיית guides — " +
                               "אז ה-vision יקרא אותם מצוין.]"
                    });
                }
            }
            catch (Exception e)
            {
                sections.Add(new Section { Source = fname, Page = null,
                    Text = "[שגיאה בקריאת PDF: " + e.Message + "]" });
            }
            return Task.FromResult(sections);
        }

        private static string MediaTypeFor(string ext)
        {
            ext = ext.ToLowerInvariant();
            if (ext == ".png") return "image/png";
            if (ext == ".gif") return "image/gif";
            if (ext == ".webp") return "image/webp";
            if (ext == ".bmp") return "image/bmp";
            return "image/jpeg"; // jpg/jpeg וברירת מחדל
        }
    }

    /// <summary>
    /// חילוץ טקסט בסיסי מ-PDF: מפענח streams עם FlateDecode (DeflateStream מובנה)
    /// ושולף מחרוזות טקסט מתוך אופרטורי Tj/TJ. לא מושלם — best effort.
    /// </summary>
    public static class PdfBasicText
    {
        public static string Extract(byte[] data)
        {
            var sb = new StringBuilder();
            string latin = Encoding.Latin1.GetString(data);

            int pos = 0;
            while (true)
            {
                int sIdx = latin.IndexOf("stream", pos, StringComparison.Ordinal);
                if (sIdx < 0) break;
                int contentStart = sIdx + "stream".Length;
                // דלג על CR/LF אחרי המילה stream
                if (contentStart < latin.Length && latin[contentStart] == '\r') contentStart++;
                if (contentStart < latin.Length && latin[contentStart] == '\n') contentStart++;

                int eIdx = latin.IndexOf("endstream", contentStart, StringComparison.Ordinal);
                if (eIdx < 0) break;

                int len = eIdx - contentStart;
                if (len > 0)
                {
                    byte[] streamBytes = new byte[len];
                    Array.Copy(data, contentStart, streamBytes, 0, len);
                    string decoded = TryInflate(streamBytes);
                    if (decoded != null)
                        ExtractShownText(decoded, sb);
                }
                pos = eIdx + "endstream".Length;
            }
            return sb.ToString().Trim();
        }

        private static string TryInflate(byte[] bytes)
        {
            // zlib header = 2 בייטים, אחריו raw deflate
            try
            {
                if (bytes.Length < 3) return null;
                using var input = new MemoryStream(bytes, 2, bytes.Length - 2);
                using var def = new DeflateStream(input, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                def.CopyTo(outMs);
                return Encoding.Latin1.GetString(outMs.ToArray());
            }
            catch
            {
                return null;
            }
        }

        // שולף טקסט מ- (..)Tj ומ- [..]TJ בתוך בלוקים של BT..ET
        private static void ExtractShownText(string content, StringBuilder sb)
        {
            // מחרוזות בסוגריים: (text)
            var matches = Regex.Matches(content, @"\(((?:\\.|[^()\\])*)\)");
            foreach (Match m in matches)
            {
                string s = m.Groups[1].Value;
                s = s.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
                s = s.Replace("\\n", "\n").Replace("\\r", "").Replace("\\t", " ");
                if (!string.IsNullOrEmpty(s)) sb.Append(s);
            }
            sb.Append('\n');
        }
    }
}
