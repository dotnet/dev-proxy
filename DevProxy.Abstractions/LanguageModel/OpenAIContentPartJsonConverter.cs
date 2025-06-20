// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProxy.Abstractions.LanguageModel;

public class OpenAIContentPartJsonConverter : JsonConverter<object>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(object);
    }

    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            var contentParts = new List<object>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    using var doc = JsonDocument.ParseValue(ref reader);

                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        var contentType = typeProp.GetString() switch
                        {
                            "text" => typeof(OpenAITextContentPart),
                            "image" => typeof(OpenAIImageContentPart),
                            "audio" => typeof(OpenAIAudioContentPart),
                            "file" => typeof(OpenAIFileContentPart),
                            _ => null
                        };
                        if (contentType is not null)
                        {
                            var contentPart = JsonSerializer.Deserialize(doc.RootElement.GetRawText(), contentType, options);
                            if (contentPart is not null)
                            {
                                contentParts.Add(contentPart);
                            }
                        }
                    }
                }
            }
            return contentParts.ToArray();
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is string str)
        {
            writer.WriteStringValue(str);
        }
        else
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}