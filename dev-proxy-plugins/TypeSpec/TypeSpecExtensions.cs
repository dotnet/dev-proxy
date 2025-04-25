// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Plugins.TypeSpec;

static class TypeSpecExtensions
{
    public static string MergeModel(this Namespace ns, Model model)
    {
        var existingModel = ns.Models.FirstOrDefault(m => m.Name == model.Name && m.IsArray == model.IsArray);
        if (existingModel is null)
        {
            ns.Models.Add(model);
            return model.Name;
        }

        foreach (var prop in model.Properties)
        {
            var existingProp = existingModel.Properties.FirstOrDefault(p => p.Name == prop.Name);
            if (existingProp is null)
            {
                existingModel.Properties.Add(prop);
            }
            else if (existingProp.Type == "null")
            {
                existingProp.Type = prop.Type;
            }
        }

        return existingModel.Name;
    }

    public static void MergeOperation(this Namespace ns, Operation op)
    {
        var existingOp = ns.Operations.FirstOrDefault(o => o.Route == op.Route && o.Method == op.Method);
        if (existingOp is null)
        {
            ns.Operations.Add(op);
            return;
        }

        foreach (var param in op.Parameters)
        {
            if (!existingOp.Parameters.Any(p => p.Name == param.Name))
            {
                existingOp.Parameters.Add(param);
            }
        }

        foreach (var response in op.Responses)
        {
            existingOp.MergeResponse(response);
        }
    }

    public static void MergeResponse(this Operation op, OperationResponseModel response)
    {
        var existingResponse = op.Responses.FirstOrDefault(r => r.StatusCode == response.StatusCode);
        if (existingResponse is null)
        {
            op.Responses.Add(response);
            return;
        }

        foreach (var header in response.Headers)
        {
            if (!existingResponse.Headers.ContainsKey(header.Key))
            {
                existingResponse.Headers.Add(header.Key, header.Value);
            }
        }

        if (!string.IsNullOrEmpty(response.BodyType) && string.IsNullOrEmpty(existingResponse.BodyType))
        {
            existingResponse.BodyType = response.BodyType;
        }
    }
}