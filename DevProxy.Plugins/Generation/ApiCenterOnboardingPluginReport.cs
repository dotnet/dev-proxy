// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Abstractions.Plugins;
using System.Globalization;
using System.Text;

namespace DevProxy.Plugins.Generation;

public sealed class ApiCenterOnboardingPluginReportExistingApiInfo
{
    public required string ApiDefinitionId { get; init; }
    public required string MethodAndUrl { get; init; }
    public required string OperationId { get; init; }
}

public sealed class ApiCenterOnboardingPluginReportNewApiInfo
{
    public required string Method { get; init; }
    public required string Url { get; init; }
}

public sealed class ApiCenterOnboardingPluginReport : IMarkdownReport, IPlainTextReport
{
    public required IEnumerable<ApiCenterOnboardingPluginReportExistingApiInfo> ExistingApis { get; init; }
    public required IEnumerable<ApiCenterOnboardingPluginReportNewApiInfo> NewApis { get; init; }

    public string? ToMarkdown()
    {
        if (NewApis.Any() &&
            ExistingApis.Any())
        {
            return null;
        }

        var sb = new StringBuilder();

        _ = sb.AppendLine("# Azure API Center onboarding report")
            .AppendLine();

        if (NewApis.Any())
        {
            var apisPerSchemeAndHost = NewApis.GroupBy(x =>
            {
                var u = new Uri(x.Url);
                return u.GetLeftPart(UriPartial.Authority);
            });

            _ = sb.AppendLine("## ⚠️ New APIs that aren't registered in Azure API Center")
                .AppendLine();

            foreach (var apiPerHost in apisPerSchemeAndHost)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"### {apiPerHost.Key}")
                    .AppendLine()
                    .AppendJoin(Environment.NewLine, apiPerHost.Select(a => $"- {a.Method} {a.Url}"))
                    .AppendLine();
            }

            _ = sb.AppendLine();
        }

        if (ExistingApis.Any())
        {
            _ = sb.AppendLine("## ✅ APIs that are already registered in Azure API Center")
                .AppendLine()
                .AppendLine("API|Definition ID|Operation ID")
                .AppendLine("---|-------------|------------")
                .AppendJoin(Environment.NewLine, ExistingApis.Select(a => $"{a.MethodAndUrl}|{a.ApiDefinitionId}|{a.OperationId}"))
                .AppendLine();
        }

        _ = sb.AppendLine();

        return sb.ToString();
    }

    public string? ToPlainText()
    {
        if (NewApis.Any() &&
            ExistingApis.Any())
        {
            return null;
        }

        var sb = new StringBuilder();

        if (NewApis.Any())
        {
            var apisPerAuthority = NewApis.GroupBy(x =>
            {
                var u = new Uri(x.Url);
                return u.GetLeftPart(UriPartial.Authority);
            });

            _ = sb.AppendLine("New APIs that aren't registered in Azure API Center:")
                .AppendLine();

            foreach (var apiPerAuthority in apisPerAuthority)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{apiPerAuthority.Key}:")
                    .AppendJoin(Environment.NewLine, apiPerAuthority.Select(a => $"  {a.Method} {a.Url}"))
                    .AppendLine();
            }

            _ = sb.AppendLine();
        }

        if (ExistingApis.Any())
        {
            var apisPerAuthority = ExistingApis.GroupBy(x =>
            {
                var methodAndUrl = x.MethodAndUrl.Split(' ');
                var u = new Uri(methodAndUrl[1]);
                return u.GetLeftPart(UriPartial.Authority);
            });

            _ = sb.AppendLine("APIs that are already registered in Azure API Center:")
                .AppendLine();

            foreach (var apiPerAuthority in apisPerAuthority)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{apiPerAuthority.Key}:")
                    .AppendJoin(Environment.NewLine, apiPerAuthority.Select(a => $"  {a.MethodAndUrl}"))
                    .AppendLine();
            }
        }

        return sb.ToString();
    }
}