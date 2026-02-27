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

    private static readonly Dictionary<long, Channel> ChannelCache = new();
    private static readonly Dictionary<long, List<ChatMessage>> ChatHistory = new();

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

        await client.BotService.JoinAllChannelsAsync();

        foreach (var planet in client.PlanetService.JoinedPlanets)
        {
            foreach (var channel in planet.Channels)
            {
                ChannelCache[channel.Id] = channel;
            }
        }

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

            var content   = message.Content;
            var channelId = message.ChannelId;
            var member    = await message.FetchAuthorMemberAsync();
            var ping      = $"«@m-{member.Id}»";

            if (content.StartsWith("s.cm"))
            {
                ChatHistory.Remove(channelId);
                await Utils.SendReplyAsync(ChannelCache, channelId, $"{ping} Channel memory cleared.");
                return;
            }

            if (content.StartsWith("s.source"))
            {
                await Utils.SendReplyAsync(ChannelCache, channelId, $"{ping} You can find my source code here: https://github.com/SkyJoshua/SkyAI");
            }

            if (!content.StartsWith("s.ai"))
                return;

            var prompt = content.Substring(5).Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                await Utils.SendReplyAsync(ChannelCache, channelId, $"{ping} Enter a question.");
                return;
            }

            var history = GetOrCreateHistory(channelId);

            history.Add(new ChatMessage { role = "user", content = prompt });
            TrimHistory(history);

            var payload = new
            {
                model = ModelName,
                messages = history
            };

            var json = JsonSerializer.Serialize(payload);

            var response = await httpClient.PostAsync(
                "/api/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(responseText);

            var output = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            history.Add(new ChatMessage { role = "assistant", content = output });

            output = $"{ping} {Truncate(output)}";

            await Utils.SendReplyAsync(ChannelCache, channelId, output);
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