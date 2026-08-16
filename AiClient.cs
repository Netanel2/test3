using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Madrich
{
    /// <summary>
    /// עטיפה מעל Google Gemini API — רק HttpClient מובנה, בלי חבילות.
    /// אותן חתימות מתודות כמו קודם, כדי ששאר הקוד לא ישתנה.
    /// </summary>
    public class AiClient
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _chatModel;
        private readonly string _visionModel;
        private readonly string _embedModel;

        private const string Base = "https://generativelanguage.googleapis.com/v1beta/models/";

        public AiClient(string apiKey, string chatModel, string visionModel, string embedModel)
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromMinutes(3);
            _apiKey = apiKey;
            _chatModel = chatModel;
            _visionModel = visionModel;
            _embedModel = embedModel;
        }

        private HttpRequestMessage NewRequest(string url, string jsonBody)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-goog-api-key", _apiKey);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return req;
        }

        // ================= Embeddings (batch) =================
        public async Task<List<float[]>> EmbedAsync(List<string> texts)
        {
            var result = new List<float[]>();
            const int B = 50; // עד 100 לבקשה; 50 בטוח
            string modelPath = "models/" + _embedModel;

            for (int i = 0; i < texts.Count; i += B)
            {
                var slice = texts.GetRange(i, Math.Min(B, texts.Count - i));
                var requests = new List<object>();
                foreach (var t in slice)
                {
                    requests.Add(new
                    {
                        model = modelPath,
                        content = new { parts = new object[] { new { text = t } } }
                    });
                }
                var body = new { requests };
                string json = JsonSerializer.Serialize(body);

                string url = Base + _embedModel + ":batchEmbedContents";
                using var req = NewRequest(url, json);
                using var resp = await _http.SendAsync(req);
                string raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception("Embeddings error: " + raw);

                using var doc = JsonDocument.Parse(raw);
                foreach (var emb in doc.RootElement.GetProperty("embeddings").EnumerateArray())
                {
                    var values = emb.GetProperty("values");
                    var vec = new float[values.GetArrayLength()];
                    int k = 0;
                    foreach (var v in values.EnumerateArray())
                        vec[k++] = (float)v.GetDouble();
                    result.Add(vec);
                }
            }
            return result;
        }

        // ================= Chat (טקסט) =================
        public async Task<string> ChatAsync(string systemPrompt, string userPrompt,
                                            double temperature = 0.2)
        {
            var body = new
            {
                systemInstruction = new { parts = new object[] { new { text = systemPrompt } } },
                contents = new object[]
                {
                    new { role = "user", parts = new object[] { new { text = userPrompt } } }
                },
                generationConfig = new { temperature, maxOutputTokens = 4096 }
            };
            return await GenerateAsync(_chatModel, body);
        }

        // ================= Vision (תיאור תמונה) =================
        public async Task<string> DescribeImageAsync(byte[] imageBytes, string mediaType,
                                                     string prompt)
        {
            string b64 = Convert.ToBase64String(imageBytes);
            var body = new
            {
                contents = new object[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = prompt },
                            new { inlineData = new { mimeType = mediaType, data = b64 } }
                        }
                    }
                },
                generationConfig = new { temperature = 0.1, maxOutputTokens = 1500 }
            };
            return await GenerateAsync(_visionModel, body);
        }

        // ================= משותף: generateContent =================
        private async Task<string> GenerateAsync(string model, object body)
        {
            string json = JsonSerializer.Serialize(body);
            string url = Base + model + ":generateContent";
            using var req = NewRequest(url, json);
            using var resp = await _http.SendAsync(req);
            string raw = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception("Gemini error: " + raw);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
            {
                // ייתכן שנחסם ע"י מסנן בטיחות
                if (root.TryGetProperty("promptFeedback", out var pf))
                    return "[התוכן נחסם ע\"י מסנן הבטיחות של Gemini: " + pf.ToString() + "]";
                return "[לא התקבלה תשובה מ-Gemini]";
            }

            var first = cands[0];
            if (!first.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts))
            {
                string reason = first.TryGetProperty("finishReason", out var fr)
                    ? fr.GetString() : "unknown";
                return "[Gemini לא החזיר טקסט (finishReason: " + reason + ")]";
            }

            var sb = new StringBuilder();
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());

            return sb.ToString().Trim();
        }
    }
}
