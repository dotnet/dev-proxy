using Scriban;
using YamlDotNet.Serialization;

namespace DevProxy.Abstractions.Prompty;

public class ChatMessage
{
    public string? Role { get; set; }
    public string? Text { get; set; }
}

public class ModelConfiguration
{
    public string? Api { get; set; }
    [YamlMember(Alias = "parameters")]
#pragma warning disable CA2227 // we need this for deserialization
    public Dictionary<string, object>? Options { get; set; }
#pragma warning restore CA2227
}

public class Prompt
{
    public IEnumerable<string>? Authors { get; set; }
    public string? Description { get; set; }
    public IEnumerable<ChatMessage>? Messages { get; set; }
    public ModelConfiguration? Model { get; set; }
    public string? Name { get; set; }
#pragma warning disable CA2227 // we need this for deserialization
    public Dictionary<string, object>? Sample { get; set; }
#pragma warning restore CA2227

    public static Prompt FromMarkdown(string markdown)
    {
        var (prompt, content) = markdown.ParseMarkdown<Prompt>();
        prompt ??= new();

        if (content is not null)
        {
            prompt.Messages = GetMessages(content);
        }

        return prompt;
    }

    public IEnumerable<ChatMessage> Prepare(Dictionary<string, object>? inputs, bool mergeSample = false)
    {
        inputs ??= [];

        if (mergeSample && Sample is not null)
        {
            foreach (var kvp in Sample)
            {
                inputs[kvp.Key] = kvp.Value;
            }
        }

        var messages = new List<ChatMessage>();

        foreach (var message in Messages ?? [])
        {
            if (message.Text is null)
            {
                continue;
            }

            var template = Template.Parse(message.Text);
            messages.Add(new()
            {
                Role = message.Role,
                Text = template.Render(inputs)
            });
        }

        return messages;
    }

    private static List<ChatMessage> GetMessages(string markdown)
    {
        var messageTypes = new[] { "system", "user", "assistant" };
        var messages = new List<ChatMessage>();
        var lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ChatMessage? currentMessage = null;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (messageTypes.Any(type => trimmedLine.StartsWith($"{type}:", StringComparison.OrdinalIgnoreCase)))
            {
                if (currentMessage is not null)
                {
                    messages.Add(currentMessage);
                }

                var role = trimmedLine.Split(':')[0].Trim().ToLowerInvariant();
                currentMessage = new ChatMessage
                {
                    Role = role,
                    Text = string.Empty
                };
                continue;
            }

            if (currentMessage is not null)
            {
                currentMessage.Text += line;
            }
        }
        if (currentMessage is not null)
        {
            messages.Add(currentMessage);
        }

        return messages;
    }
}