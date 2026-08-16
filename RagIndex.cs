using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Madrich
{
    public class Chunk
    {
        public string Source { get; set; }
        public int? Page { get; set; }
        public string Text { get; set; }
        public float[] Embedding { get; set; }
    }

    public class IndexFile
    {
        public string Model { get; set; }
        public List<Chunk> Items { get; set; } = new List<Chunk>();
    }

    public static class RagIndex
    {
        public const int ChunkChars = 1500;
        public const int ChunkOverlap = 250;
        public const int TopK = 6;

        // ---------- חלוקה לקטעים ----------
        public static List<Chunk> ChunkSections(List<Section> sections)
        {
            var chunks = new List<Chunk>();
            foreach (var sec in sections)
            {
                string text = (sec.Text ?? "").Trim();
                if (text.Length == 0) continue;

                int start = 0;
                while (start < text.Length)
                {
                    int len = Math.Min(ChunkChars, text.Length - start);
                    string piece = text.Substring(start, len).Trim();
                    if (piece.Length > 0)
                        chunks.Add(new Chunk { Source = sec.Source, Page = sec.Page, Text = piece });

                    if (start + ChunkChars >= text.Length) break;
                    start += ChunkChars - ChunkOverlap;
                }
            }
            return chunks;
        }

        // ---------- שמירה / טעינה ----------
        public static void Save(string path, IndexFile index)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var opts = new JsonSerializerOptions { WriteIndented = false };
            File.WriteAllText(path, JsonSerializer.Serialize(index, opts));
        }

        public static IndexFile Load(string path)
        {
            if (!File.Exists(path)) return new IndexFile();
            string raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IndexFile>(raw) ?? new IndexFile();
        }

        // ---------- חיפוש ----------
        public static async Task<List<(double score, Chunk chunk)>> SearchAsync(
            AiClient ai, string query, IndexFile index, int topK = TopK)
        {
            var results = new List<(double, Chunk)>();
            if (index.Items.Count == 0) return results;

            var qvecList = await ai.EmbedAsync(new List<string> { query });
            var q = qvecList[0];

            foreach (var it in index.Items)
                results.Add((Cosine(q, it.Embedding), it));

            results.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            if (results.Count > topK) results = results.GetRange(0, topK);
            return results;
        }

        public static double Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na == 0 || nb == 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }
    }
}
