using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Madrich
{
    public class WebServer
    {
        private readonly AiClient _ai;
        private readonly IndexFile _index;
        private readonly string _guidesDir;
        private readonly string _indexPath;
        private readonly object _lock = new object();
        private readonly string _html;

        public const string SystemPrompt =
@"אתה עוזר פנימי שעונה על שאלות לפי מדריכי הפעלה של מערכות.
ענה בעברית בלבד, בצורה ברורה ומעשית — שלב-אחר-שלב כשמדובר ב'איך עושים משהו'.
השתמש *אך ורק* במידע שמופיע בקטעי המדריכים שסופקו לך. אל תמציא צעדים.
אם התשובה לא נמצאת במדריכים, אמור זאת בכנות והצע איפה כדאי לחפש.
בסוף התשובה ציין מאיזה מדריך (ושם עמוד אם יש) לקוח המידע.";

        public WebServer(AiClient ai, IndexFile index, string guidesDir, string indexPath)
        {
            _ai = ai;
            _index = index;
            _guidesDir = guidesDir;
            _indexPath = indexPath;
            _html = LoadHtml();
        }

        // טוען את ה-HTML מתוך משאב מוטמע באסמבלי — אף פעם לא "לא נמצא"
        private static string LoadHtml()
        {
            var asm = typeof(WebServer).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = asm.GetManifestResourceStream(name);
                    using var r = new StreamReader(s, Encoding.UTF8);
                    return r.ReadToEnd();
                }
            }
            return "<html dir=\"rtl\"><body style=\"font-family:sans-serif;background:#0d0f14;color:#fff;padding:40px\">" +
                   "<h2>מרכז המדריכים</h2><p>ה-HTML לא נטען. ודא ש-index.html מוגדר כ-EmbeddedResource ב-csproj.</p></body></html>";
        }

        public async Task RunAsync(string prefix)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            Console.WriteLine("==================================================");
            Console.WriteLine("  שרת המדריכים עלה!");
            Console.WriteLine("  פתח בדפדפן:  " + prefix);
            Console.WriteLine("  אפשר לגרור מדריכים ישר לצ'אט. לעצירה: Ctrl+C");
            Console.WriteLine("==================================================");

            while (true)
            {
                var ctx = await listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;
                if (path == "/" || path == "/index.html")
                    await WriteAsync(ctx, "text/html; charset=utf-8", _html);
                else if (path == "/api/sources")
                    await ServeSourcesAsync(ctx);
                else if (path == "/api/chat" && ctx.Request.HttpMethod == "POST")
                    await ServeChatAsync(ctx);
                else if (path == "/api/upload" && ctx.Request.HttpMethod == "POST")
                    await ServeUploadAsync(ctx);
                else
                {
                    ctx.Response.StatusCode = 404;
                    await WriteAsync(ctx, "text/plain", "Not found");
                }
            }
            catch (Exception e)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    await WriteJson(ctx, new { error = e.Message });
                }
                catch { }
            }
        }

        private async Task ServeSourcesAsync(HttpListenerContext ctx)
        {
            List<string> srcs;
            int count;
            lock (_lock)
            {
                var set = new HashSet<string>();
                foreach (var it in _index.Items) set.Add(it.Source);
                srcs = new List<string>(set);
                count = _index.Items.Count;
            }
            await WriteJson(ctx, new { sources = srcs, chunks = count });
        }

        // ================= העלאת מדריך דרך הצ'אט =================
        private async Task ServeUploadAsync(HttpListenerContext ctx)
        {
            string body = await ReadBodyAsync(ctx);
            string filename = null, dataB64 = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("filename", out var f)) filename = f.GetString();
                if (doc.RootElement.TryGetProperty("data", out var d)) dataB64 = d.GetString();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(dataB64))
            {
                ctx.Response.StatusCode = 400;
                await WriteJson(ctx, new { ok = false, error = "חסר שם קובץ או תוכן" });
                return;
            }

            // שמור את הקובץ לתיקיית guides
            filename = Path.GetFileName(filename); // מנקה נתיבים
            Directory.CreateDirectory(_guidesDir);
            string savePath = Path.Combine(_guidesDir, filename);
            byte[] bytes = Convert.FromBase64String(dataB64);
            File.WriteAllBytes(savePath, bytes);

            // חלץ תוכן (כולל תיאור תמונות עם Gemini)
            var sections = await Extractors.ExtractAsync(_ai, savePath);
            var chunks = RagIndex.ChunkSections(sections);
            if (chunks.Count == 0)
            {
                await WriteJson(ctx, new { ok = false, error = "לא נמצא תוכן לאנדקס בקובץ" });
                return;
            }

            // embeddings (רשת — מחוץ ל-lock)
            var texts = new List<string>();
            foreach (var c in chunks) texts.Add(c.Text);
            var vectors = await _ai.EmbedAsync(texts);
            for (int i = 0; i < chunks.Count; i++) chunks[i].Embedding = vectors[i];

            int total;
            lock (_lock)
            {
                _index.Items.AddRange(chunks);
                RagIndex.Save(_indexPath, _index);
                total = _index.Items.Count;
            }

            await WriteJson(ctx, new { ok = true, filename, added = chunks.Count, total });
        }

        // ================= צ'אט =================
        private async Task ServeChatAsync(HttpListenerContext ctx)
        {
            string body = await ReadBodyAsync(ctx);
            string message = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
            }
            catch { }

            message = message.Trim();
            if (message.Length == 0)
            {
                ctx.Response.StatusCode = 400;
                await WriteJson(ctx, new { error = "שאלה ריקה" });
                return;
            }

            bool empty;
            lock (_lock) { empty = _index.Items.Count == 0; }
            if (empty)
            {
                ctx.Response.StatusCode = 400;
                await WriteJson(ctx, new { error = "עדיין אין מדריכים. גרור מדריך לצ'אט או לחץ '➕ העלה מדריך'." });
                return;
            }

            // embedding לשאלה (רשת — מחוץ ל-lock)
            var qvec = (await _ai.EmbedAsync(new List<string> { message }))[0];

            // דירוג הקטעים (מהיר — בתוך lock)
            var top = new List<Chunk>();
            lock (_lock)
            {
                var scored = new List<(double, Chunk)>();
                foreach (var it in _index.Items)
                    scored.Add((RagIndex.Cosine(qvec, it.Embedding), it));
                scored.Sort((a, b) => b.Item1.CompareTo(a.Item1));
                for (int i = 0; i < Math.Min(RagIndex.TopK, scored.Count); i++)
                    top.Add(scored[i].Item2);
            }

            var sources = new List<string>();
            var sbCtx = new StringBuilder();
            int n = 1;
            foreach (var ch in top)
            {
                string loc = ch.Source + (ch.Page.HasValue ? " · עמ' " + ch.Page.Value : "");
                if (!sources.Contains(loc)) sources.Add(loc);
                sbCtx.Append("[מקור " + n + " — " + loc + "]\n" + ch.Text + "\n\n---\n\n");
                n++;
            }

            string userPrompt = "קטעים רלוונטיים מהמדריכים:\n\n" + sbCtx + "=====\nשאלה: " + message;
            string answer = await _ai.ChatAsync(SystemPrompt, userPrompt);

            await WriteJson(ctx, new { answer, sources });
        }

        // ================= עזרים =================
        private static async Task<string> ReadBodyAsync(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        private static async Task WriteJson(HttpListenerContext ctx, object obj)
        {
            await WriteAsync(ctx, "application/json; charset=utf-8", JsonSerializer.Serialize(obj));
        }

        private static async Task WriteAsync(HttpListenerContext ctx, string mime, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentType = mime;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
