using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Madrich
{
    public static class Program
    {
        static string ContentRoot => Directory.GetCurrentDirectory();
        static string GuidesDir => Path.Combine(ContentRoot, "guides");
        static string DataDir => Path.Combine(ContentRoot, "data");
        static string IndexPath => Path.Combine(DataDir, "index.json");

        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            LoadDotEnv();

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "serve";

            string apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine("❌ לא נמצא GEMINI_API_KEY.");
                Console.WriteLine("   צור קובץ .env בתיקייה עם השורה:");
                Console.WriteLine("   GEMINI_API_KEY=AIza...");
                Console.WriteLine("   (משיגים מפתח ב: https://aistudio.google.com/apikey )");
                return 1;
            }

            string chatModel = Env("CHAT_MODEL", "gemini-2.5-flash");
            string visionModel = Env("VISION_MODEL", "gemini-2.5-flash");
            string embedModel = Env("EMBED_MODEL", "gemini-embedding-001");

            var ai = new AiClient(apiKey, chatModel, visionModel, embedModel);
            Directory.CreateDirectory(GuidesDir);
            Directory.CreateDirectory(DataDir);

            switch (cmd)
            {
                case "ingest":
                    await IngestAsync(ai, embedModel);
                    return 0;

                case "serve":
                default:
                    var index = RagIndex.Load(IndexPath);
                    Console.WriteLine(index.Items.Count == 0
                        ? "ℹ️  עוד אין מדריכים — אפשר לגרור אותם ישר לצ'אט."
                        : $"ℹ️  נטענו {index.Items.Count} קטעים קיימים.");
                    var server = new WebServer(ai, index, GuidesDir, IndexPath);
                    await server.RunAsync("http://127.0.0.1:5000/");
                    return 0;
            }
        }

        // ingest נשאר זמין למי שרוצה לזרוק לתיקייה ולהריץ בבת אחת
        static async Task IngestAsync(AiClient ai, string embedModel)
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".docx", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".txt", ".md" };

            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(GuidesDir, "*.*", SearchOption.AllDirectories))
                if (exts.Contains(Path.GetExtension(f))) files.Add(f);
            files.Sort();

            if (files.Count == 0)
            {
                Console.WriteLine("⚠️  לא נמצאו קבצים בתיקייה: " + GuidesDir);
                return;
            }

            Console.WriteLine($"נמצאו {files.Count} קבצים. מתחיל עיבוד...\n");
            var allSections = new List<Section>();
            int n = 0;
            foreach (var path in files)
            {
                n++;
                Console.WriteLine($"[{n}/{files.Count}] קורא: {Path.GetFileName(path)} ...");
                try
                {
                    var secs = await Extractors.ExtractAsync(ai, path);
                    allSections.AddRange(secs);
                    Console.WriteLine($"          → {secs.Count} סקשנים");
                }
                catch (Exception e) { Console.WriteLine($"          ✗ שגיאה: {e.Message}"); }
            }

            var chunks = RagIndex.ChunkSections(allSections);
            Console.WriteLine($"\nסה\"כ {chunks.Count} קטעים. מחשב embeddings...");
            if (chunks.Count == 0) return;

            var texts = new List<string>();
            foreach (var c in chunks) texts.Add(c.Text);
            var vectors = await ai.EmbedAsync(texts);
            for (int i = 0; i < chunks.Count; i++) chunks[i].Embedding = vectors[i];

            var index = new IndexFile { Model = embedModel, Items = chunks };
            RagIndex.Save(IndexPath, index);
            Console.WriteLine($"\n✅ נשמר אינדקס: {IndexPath}  ({chunks.Count} קטעים)");
            Console.WriteLine("הרץ:  dotnet run -- serve");
        }

        static string Env(string key, string fallback)
        {
            var v = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        static void LoadDotEnv()
        {
            string envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            if (!File.Exists(envPath)) return;
            foreach (var line in File.ReadAllLines(envPath))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string key = t.Substring(0, eq).Trim();
                string val = t.Substring(eq + 1).Trim();
                if (Environment.GetEnvironmentVariable(key) == null)
                    Environment.SetEnvironmentVariable(key, val);
            }
        }
    }
}
