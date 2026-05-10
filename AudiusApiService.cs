using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NezumiRadio
{
    public class AudiusApiService
    {
        private readonly HttpClient _http;
        private string? _node;
        private readonly List<string> _fallbackNodes = new() 
        { 
            "https://audius-metadata-5.figment.io", 
            "https://discovery-provider.audius.co", 
            "https://audius-discovery-1.algonode.cloud" 
        };

        public AudiusApiService(HttpClient http) => _http = http;

        public async Task<List<string>> GetTracksByGenreAsync(string genre)
        {
            if (_node == null) {
                try {
                    var r = await _http.GetStringAsync("https://api.audius.co");
                    using var d = JsonDocument.Parse(r);
                    var n = d.RootElement.GetProperty("data").EnumerateArray().Select(x => x.ToString()).ToList();
                    _node = n[new Random().Next(n.Count)];
                } catch { _node = _fallbackNodes[new Random().Next(_fallbackNodes.Count)]; }
            }
            try {
                var url = $"{_node}/v1/tracks/trending?genre={Uri.EscapeDataString(genre)}&limit=100&time=month";
                var resp = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);
                return doc.RootElement.GetProperty("data").EnumerateArray()
                    .Select(t => $"{_node}/v1/tracks/{t.GetProperty("id").GetString()}/stream?app_name=nezumi_radio")
                    .ToList();
            } catch { 
                _node = null; 
                return new List<string>(); 
            }
        }
    }
}
