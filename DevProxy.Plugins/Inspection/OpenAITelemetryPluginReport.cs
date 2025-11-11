using DevProxy.Abstractions.Plugins;
using DevProxy.Abstractions.Utils;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Plugins.Inspection;

public class OpenAITelemetryPluginReportModelUsageInformation
{
    public required long CompletionTokens { get; init; }
    public double Cost { get; set; }
    public required string Model { get; init; }
    public required long PromptTokens { get; init; }
    public long CachedTokens { get; init; }
}

public class OpenAITelemetryPluginReport : IMarkdownReport, IPlainTextReport, IJsonReport
{
    public required string Application { get; init; }
    public required string Currency { get; init; }
    public required string Environment { get; init; }
    [JsonIgnore]
    public bool IncludeCosts { get; set; }
    public required Dictionary<string, List<OpenAITelemetryPluginReportModelUsageInformation>> ModelUsage { get; init; } = [];

    public string FileExtension => ".json";

    public object ToJson() => JsonSerializer.Serialize(this, ProxyUtils.JsonSerializerOptions);

    public string? ToMarkdown()
    {
        var totalTokens = 0L;
        var totalPromptTokens = 0L;
        var totalCompletionTokens = 0L;
        var totalCachedTokens = 0L;
        var totalCost = 0.0;
        var totalRequests = 0;

        var sb = new StringBuilder();
        _ = sb
            .AppendLine(CultureInfo.InvariantCulture, $"# LLM usage report for {Application} in {Environment}")
            .AppendLine()
            .Append("Model|Requests|Prompt Tokens|Completion Tokens|Cached Tokens|Total Tokens");

        if (IncludeCosts)
        {
            _ = sb.Append("|Total Cost");
        }

        _ = sb
            .AppendLine()
            .Append(":----|-------:|------------:|----------------:|-----------:|-----------:");

        if (IncludeCosts)
        {
            _ = sb.Append("|---------:");
        }

        _ = sb.AppendLine();

        foreach (var modelUsage in ModelUsage.OrderBy(m => m.Key))
        {
            var promptTokens = modelUsage.Value.Sum(u => u.PromptTokens);
            var completionTokens = modelUsage.Value.Sum(u => u.CompletionTokens);
            var cachedTokens = modelUsage.Value.Sum(u => u.CachedTokens);
            var tokens = promptTokens + completionTokens;

            totalPromptTokens += promptTokens;
            totalCompletionTokens += completionTokens;
            totalCachedTokens += cachedTokens;
            totalTokens += tokens;
            totalRequests += modelUsage.Value.Count;

            _ = sb
                .Append(modelUsage.Key)
                .Append('|').Append(totalRequests)
                .Append('|').Append(promptTokens)
                .Append('|').Append(completionTokens)
                .Append('|').Append(cachedTokens)
                .Append('|').Append(tokens);

            if (IncludeCosts)
            {
                var cost = modelUsage.Value.Sum(u => u.Cost);
                totalCost += cost;
                _ = sb.Append('|').Append(FormatCost(cost, Currency));
            }

            _ = sb.AppendLine();
        }

        _ = sb
            .Append("**Total**")
            .Append('|').Append(CultureInfo.CurrentCulture, $"**{totalRequests}**")
            .Append('|').Append(CultureInfo.CurrentCulture, $"**{totalPromptTokens}**")
            .Append('|').Append(CultureInfo.CurrentCulture, $"**{totalCompletionTokens}**")
            .Append('|').Append(CultureInfo.CurrentCulture, $"**{totalCachedTokens}**")
            .Append('|').Append(CultureInfo.CurrentCulture, $"**{totalTokens}**");

        if (IncludeCosts)
        {
            _ = sb.Append('|').Append(CultureInfo.CurrentCulture, $"**{FormatCost(totalCost, Currency)}**");
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        var totalTokens = 0L;
        var totalPromptTokens = 0L;
        var totalCompletionTokens = 0L;
        var totalCachedTokens = 0L;
        var totalCost = 0.0;

        var sb = new StringBuilder();
        _ = sb
            .AppendLine(CultureInfo.InvariantCulture, $"LLM USAGE REPORT FOR {Application} IN {Environment}")
            .AppendLine()
            .AppendLine("PER MODEL USAGE")
            .AppendLine();

        foreach (var modelUsage in ModelUsage.OrderBy(m => m.Key))
        {
            var promptTokens = modelUsage.Value.Sum(u => u.PromptTokens);
            var completionTokens = modelUsage.Value.Sum(u => u.CompletionTokens);
            var cachedTokens = modelUsage.Value.Sum(u => u.CachedTokens);
            var tokens = promptTokens + completionTokens;

            totalPromptTokens += promptTokens;
            totalCompletionTokens += completionTokens;
            totalCachedTokens += cachedTokens;
            totalTokens += tokens;

            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"MODEL: {modelUsage.Key}")
                .AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"Requests: {modelUsage.Value.Count}")
                .AppendLine(CultureInfo.InvariantCulture, $"Prompt Tokens: {promptTokens}")
                .AppendLine(CultureInfo.InvariantCulture, $"Completion Tokens: {completionTokens}")
                .AppendLine(CultureInfo.InvariantCulture, $"Cached Tokens: {cachedTokens}")
                .AppendLine(CultureInfo.InvariantCulture, $"Total Tokens: {tokens}");

            if (IncludeCosts)
            {
                var cost = modelUsage.Value.Sum(u => u.Cost);
                totalCost += cost;
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Cost: {FormatCost(cost, Currency)}");
            }

            _ = sb.AppendLine();
        }

        _ = sb
            .AppendLine("TOTALS")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Prompt Tokens: {totalPromptTokens}")
            .AppendLine(CultureInfo.InvariantCulture, $"Completion Tokens: {totalCompletionTokens}")
            .AppendLine(CultureInfo.InvariantCulture, $"Cached Tokens: {totalCachedTokens}")
            .AppendLine(CultureInfo.InvariantCulture, $"Total Tokens: {totalTokens}");

        if (IncludeCosts)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Cost: {FormatCost(totalCost, Currency)}");
        }
        return sb.ToString();
    }

    private static string FormatCost(double cost, string currency)
    {
        return $"{cost.ToString("#,##0.00########", CultureInfo.InvariantCulture)} {currency}";
    }
}
