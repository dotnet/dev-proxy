// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace DevProxy.Abstractions.LanguageModel;

public class PricesData: Dictionary<string, ModelPrices>
{
    public bool TryGetModelPrices(string modelName, out ModelPrices? prices)
    {
        prices = new ModelPrices();

        if (string.IsNullOrEmpty(modelName))
        {
            return false;
        }

        // Try exact match first
        if (TryGetValue(modelName, out prices))
        {
            return true;
        }

        // Try to find a matching prefix
        // This handles cases like "gpt-4-turbo-2024-04-09" matching with "gpt-4"
        var matchingModel = Keys
            .Where(k => modelName.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();

        if (matchingModel != null && TryGetValue(matchingModel, out prices))
        {
            return true;
        }

        return false;
    }

    public (double Input, double Output) CalculateCost(string modelName, long inputTokens, long outputTokens)
    {
        if (!TryGetModelPrices(modelName, out var prices))
        {
            return (0, 0);
        }

        Debug.Assert(prices != null, "Prices data should not be null here.");

        // Prices in the data are per 1M tokens
        var inputCost = prices.Input * (inputTokens / 1_000_000.0);
        var outputCost = prices.Output * (outputTokens / 1_000_000.0);

        return (inputCost, outputCost);
    }
}

public class ModelPrices
{
    public double Input { get; set; }
    public double Output { get; set; }
}
