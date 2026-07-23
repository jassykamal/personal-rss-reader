using System.Text;
using System.Text.Json;

namespace RssReader;

public interface IDeepSeekService
{
    Task<string> SummarizeAsync(
        string title, string description, string feedTitle, string contentType,
        CancellationToken ct = default);
}

public class DeepSeekService : IDeepSeekService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<DeepSeekService> _logger;

    public DeepSeekService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<DeepSeekService> logger)
    {
        _http = httpFactory.CreateClient("DeepSeek");
        _config = config;
        _logger = logger;
    }

    public async Task<string> SummarizeAsync(
        string title, string description, string feedTitle, string contentType,
        CancellationToken ct = default)
    {
        var apiKey = _config["DeepSeek:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("DeepSeek API key is not configured.");

        var model = _config["DeepSeek:Model"] ?? "deepseek-chat";

        var prompt = BuildPrompt(title, description, feedTitle, contentType);

        var requestBody = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "You are a helpful assistant that summarizes articles and podcast episodes. Return only the summary text, no preamble." },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 600,
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("DeepSeek API request timed out for: {Title}", title);
            return "The summary request timed out. Please try again.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "DeepSeek API network error for: {Title}", title);
            return "Could not reach the AI service. Check your network connection and try again.";
        }

        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "DeepSeek API returned {StatusCode}: {Body}",
                (int)response.StatusCode,
                responseBody.Length > 300 ? responseBody[..300] : responseBody);

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "The AI service API key is invalid or expired.",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "The AI service is rate-limited. Please try again in a moment.",
                System.Net.HttpStatusCode.ServiceUnavailable =>
                    "The AI service is temporarily unavailable. Please try again later.",
                _ => $"The AI service returned an error ({(int)response.StatusCode}). Please try again."
            };
        }

        string summary;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                _logger.LogWarning("DeepSeek returned empty choices for: {Title}", title);
                return "The AI service returned an empty response. Please try again.";
            }

            var message = choices[0].GetProperty("message");
            summary = message.GetProperty("content").GetString() ?? "";
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse DeepSeek response for: {Title}", title);
            return "Could not parse the AI service response. Please try again.";
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogWarning("DeepSeek returned empty summary for: {Title}", title);
            return "The AI service returned an empty summary. Please try again.";
        }

        _logger.LogInformation("DeepSeek summary generated for: {Title}", title);
        return summary.Trim();
    }

    private static string BuildPrompt(
        string title, string description, string feedTitle, string contentType)
    {
        var isPodcast = contentType.Equals("Podcast", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("Summarize the following ");
        sb.Append(isPodcast ? "podcast episode" : "news article");
        sb.AppendLine(":\n");

        sb.Append("Title: ").AppendLine(title);
        sb.Append("Source: ").AppendLine(feedTitle);

        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append("Content: ").AppendLine(description);
        }

        sb.AppendLine();

        if (isPodcast)
        {
            sb.AppendLine("Provide a short overview of the episode, the main topics discussed,");
            sb.AppendLine("and mention any guests if they appear in the title or description.");
            sb.AppendLine("Note that this summary is based on available metadata only (title and description).");
        }
        else
        {
            sb.AppendLine("Provide 3-5 concise bullet points covering the key facts.");
            sb.AppendLine("Use a neutral tone. Do not add information not present in the content.");
            sb.AppendLine("If the content is insufficient to form a meaningful summary, state that explicitly.");
        }

        return sb.ToString();
    }
}
