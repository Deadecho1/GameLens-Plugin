using Godot;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameLensAnalytics.Runtime
{
    public sealed class SessionService
    {
        private readonly System.Net.Http.HttpClient _http = new System.Net.Http.HttpClient();
        private readonly string endpointSession = "/api/v1/collect/session";

        // create a new session on the server, returning the session id
        public async Task<string> CreateSessionAsync(string endpointBase, string gameId)
        {
            // POST {game_id, started_at}
            var url = $"{endpointBase.TrimEnd('/')}{endpointSession}";

            var body = new
            {
                game_id = gameId,
                started_at = DateTime.UtcNow.ToString("o"),
                // optional: client_info later
            };

            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            var respText = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"CreateSession failed: {(int)resp.StatusCode} {respText}");

            using var doc = JsonDocument.Parse(respText);
            if (!doc.RootElement.TryGetProperty("session_id", out var sidEl))
                throw new Exception($"CreateSession response missing session_id: {respText}");

            return sidEl.GetString();
        }
    }
}