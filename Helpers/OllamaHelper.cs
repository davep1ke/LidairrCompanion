using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using LidairrCompanion.Models;

namespace LidairrCompanion.Helpers
{
    public class OllamaHelper
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;

        public OllamaHelper()
        {
            _baseUrl = AppSettings.GetValue(SettingKey.OllamaURL).TrimEnd('/');
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromMinutes(5);


            // test
            // curl http://nasp1ke.local:30068/api/generate -d "{\"model\":\"qwen3:4b\",\"prompt\":\"hi\",\"stream\":false}"
        }

        public async Task<string> SendPromptAsync(string prompt)
        {
            var requestBody = new
            {
                model = AppSettings.GetValue(SettingKey.OllamaModel),
                prompt = $"Do not provide reasoning, just give the answer. {prompt}",
                stream = true // Enable streaming
            };
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync($"{_baseUrl}/api/generate", content);
            response.EnsureSuccessStatusCode();

            var resultBuilder = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var responseElement))
                {
                    resultBuilder.Append(responseElement.GetString());
                }
            }

            return resultBuilder.ToString();
        }
    }
}