using DotNetEnv;
using Valour.Sdk.Client;
using Valour.Sdk.Models;
using SkyAI;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SkyAI;

public class ChatMessage
{
    public string role { get; set; } = "";
    public string content { get; set; } = "";
}

public class Program
{
    const int MaxHistoryMessages = 10;
    const int MaxResponseLength = 2048;
    const string ModelName = "llama3.1:latest";
    const string prefix = "s.";
    const string sourceLink  = "https://github.com/SkyJoshua/SkyAI";

    private static readonly Dictionary<long, Channel> channelCache = new();
    private static readonly Dictionary<long, List<ChatMessage>> ChatHistory = new();
    private static readonly HashSet<long> InitializedPlanets = new();

    public static async Task Main()
    {
        Env.Load();

        var token      = Environment.GetEnvironmentVariable("TOKEN");
        var openWebApi = Environment.GetEnvironmentVariable("OPENWEBAPI");
        var openWebUrl = Environment.GetEnvironmentVariable("OPENWEBURL");

        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(openWebApi) ||
            string.IsNullOrWhiteSpace(openWebUrl))
        {
            Console.WriteLine("Missing required environment variables.");
            return;
        }

        var client = new ValourClient("https://api.valour.gg/");
        client.SetupHttpClient();

        var loginResult = await client.InitializeUser(token);
        if (!loginResult.Success)
        {
            Console.WriteLine($"Login Failed: {loginResult.Message}");
            return;
        }

        Console.WriteLine($"Logged in as {client.Me.Name}");

        await Utils.InitializePlanetsAsync(client, channelCache, InitializedPlanets);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(openWebUrl)
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", openWebApi);

        client.MessageService.MessageReceived += async (message) =>
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                return;

            var content     = message.Content;
            var channelId   = message.ChannelId;
            var member      = await message.FetchAuthorMemberAsync();
            var ping        = $"«@m-{member.Id}»";

            if (content.StartsWith($"{prefix}cm"))
            {
                ChatHistory.Remove(channelId);
                await Utils.SendReplyAsync(channelCache, channelId, $"{ping} Channel memory cleared.");
                return;
            }

            if (content.StartsWith($"{prefix}source"))
            {
                await Utils.SendReplyAsync(channelCache, channelId, $"{ping} You can find my source code here: {sourceLink}");
            }

            if (!content.StartsWith($"{prefix}ai"))
                return;

            var prompt = content.Substring(prefix.Length+3).Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                await Utils.SendReplyAsync(channelCache, channelId, $"{ping} Enter a question.");
                return;
            }

            var history = GetOrCreateHistory(channelId);

            history.Add(new ChatMessage { role = "user", content = $"User: {member.Name} (ID: {member.Id}) | {prompt}" });
            TrimHistory(history);

            var payload = new
            {
                model = ModelName,
                messages = history
            };

            var json = JsonSerializer.Serialize(payload);


            HttpResponseMessage response;
            try
            {
                response = await httpClient.PostAsync(
                    "/api/chat/completions",
                    new StringContent(json, Encoding.UTF8, "application/json")
                );
            }
            catch (HttpRequestException ex)
            {
                await Utils.SendReplyAsync(
                    channelCache,
                    channelId,
                    $"{ping} Cannot reach OpenWebUI server. Check OPENWEBURL.\nError: {ex.Message}"
                );
                return;
            }
            catch (TaskCanceledException)
            {
                await Utils.SendReplyAsync(
                    channelCache,
                    channelId,
                    $"{ping} Connection timed out. Check OPENWEBURL."
                );
                return;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await Utils.SendReplyAsync(
                    channelCache,
                    channelId,
                    $"{ping} OpenWebUI API key is invalid."
                );
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();

                await Utils.SendReplyAsync(
                    channelCache,
                    channelId,
                    $"{ping} OpenWebUI error {(int)response.StatusCode}:\n{errorBody}"
                );
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(responseText);

            var output = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            history.Add(new ChatMessage { role = "assistant", content = output });

            output = $"{ping} {output}";

            output = Truncate(output);

            await Utils.SendReplyAsync(channelCache, channelId, output);
        };

        Console.WriteLine("Listening...");
        await Task.Delay(Timeout.Infinite);
    }

    private static List<ChatMessage> GetOrCreateHistory(long channelId)
    {
        if (!ChatHistory.ContainsKey(channelId))
        {
            ChatHistory[channelId] = new List<ChatMessage>
            {
                new ChatMessage
                {
                    role = "system",
                    content = "You are a helpful AI assistant. Keep responses under 2048 characters."
                }
            };
        }

        return ChatHistory[channelId];
    }

    private static void TrimHistory(List<ChatMessage> history)
    {
        if (history.Count <= (MaxHistoryMessages * 2) + 1)
            return;

        var system = history[0];

        var trimmed = history
            .Skip(history.Count - (MaxHistoryMessages * 2))
            .ToList();

        trimmed.Insert(0, system);

        history.Clear();
        history.AddRange(trimmed);
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text.Length <= MaxResponseLength
            ? text
            : text.Substring(0, MaxResponseLength);
    }
}