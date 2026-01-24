// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using DevProxy.Plugins.Models;
using DevProxy.Plugins.Utils;
using System.Xml;

namespace DevProxy.Plugins.SharePoint;

enum ActionType
{
    ObjectPath,
    Query,
    SetProperty
}

enum AccessType
{
    Delegated,
    Application
}

static class CsomParser
{
    public static (IEnumerable<string> Actions, IEnumerable<string> Errors) GetActions(string xml, CsomTypesDefinition typesDefinition)
    {
        if (typesDefinition?.Types == null || string.IsNullOrEmpty(xml))
        {
            return ([], []);
        }

        var actions = new List<string>();
        var errors = new List<string>();

        try
        {
            // Load the XML
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var nsManager = new XmlNamespaceManager(doc.NameTable);
            var defaultNamespace = doc.DocumentElement?.NamespaceURI ?? string.Empty;
            if (!string.IsNullOrEmpty(defaultNamespace))
            {
                nsManager.AddNamespace("ns", defaultNamespace);
            }

            // Get the Actions element
            var actionsNode = doc.SelectSingleNode("//ns:Actions", nsManager);
            if (actionsNode == null)
            {
                errors.Add("Actions node not found in XML.");
                // If Actions node is not found, return empty list
                return (actions, errors);
            }

            // Process all child Action elements
            foreach (XmlNode actionNode in actionsNode.ChildNodes)
            {
                var actionType = GetActionType(actionNode.Name);

                // Extract ObjectPathId attribute
                var objectPathIdAttr = actionNode.Attributes?["ObjectPathId"];
                if (objectPathIdAttr == null)
                {
                    errors.Add($"ObjectPathId attribute not found for action: {actionNode.OuterXml}");
                    continue;
                }

                var objectPathId = objectPathIdAttr.Value;

                var type = GetObjectPathName(objectPathId, actionType, doc, nsManager, typesDefinition);

                if (type != null)
                {
                    actions.Add(type);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Error parsing XML: {ex.Message}");
        }

        return (actions, errors);
    }

    public static (IEnumerable<string> MinimalScopes, IEnumerable<string> UnmatchedOperations) GetMinimalScopes(IEnumerable<string> actions, AccessType accessType, CsomTypesDefinition typesDefinition)
    {
        var operationsAndScopes = typesDefinition?.Actions
            ?.Where(o => o.Value.Delegated != null || o.Value.Application != null)
            .ToDictionary(
                o => o.Key,
                o => (accessType == AccessType.Delegated ? o.Value.Delegated : o.Value.Application)!.ToArray()
            );
        return MinimalPermissionsUtils.GetMinimalScopes([.. actions], operationsAndScopes!);
    }

    private static string? GetTypeName(string objectPathId, XmlDocument doc, XmlNamespaceManager nsManager, CsomTypesDefinition typesDefinition)
    {
        var objectPath = doc.SelectSingleNode($"//ns:ObjectPaths/*[@Id='{objectPathId}']", nsManager);
        if (objectPath == null)
        {
            return null;
        }

        if (objectPath.Name is "Constructor" or
            "StaticProperty")
        {
            var typeIdAttr = objectPath.Attributes?["TypeId"];
            if (typeIdAttr != null)
            {
                var typeId = typeIdAttr.Value.Trim('{', '}');
                if (typesDefinition?.Types?.TryGetValue(typeId, out var typeName) == true)
                {
                    if (objectPath.Name == "StaticProperty")
                    {
                        var nameAttr = objectPath.Attributes?["Name"];
                        if (nameAttr != null)
                        {
                            return $"{typeName}.{nameAttr.Value}";
                        }
                    }
                    return typeName;
                }
                else
                {
                    return null;
                }
            }
            return null;
        }

        var parentIdAttr = objectPath.Attributes?["ParentId"];
        if (parentIdAttr == null)
        {
            return null;
        }
        var parentId = parentIdAttr.Value;

        return GetTypeName(parentId, doc, nsManager, typesDefinition);
    }

    private static string? GetObjectPathName(string objectPathId, ActionType actionType, XmlDocument doc, XmlNamespaceManager nsManager, CsomTypesDefinition typesDefinition)
    {
        var objectPath = doc.SelectSingleNode($"//ns:ObjectPaths/*[@Id='{objectPathId}']", nsManager);
        if (objectPath == null)
        {
            return null;
        }

        var typeName = GetTypeName(objectPathId, doc, nsManager, typesDefinition);
        if (typeName == null)
        {
            return null;
        }

        if (objectPath.Name == "Constructor")
        {
            var suffix = actionType == ActionType.Query ? "query" : "ctor";
            return $"{typeName}.{suffix}";
        }

        if (objectPath.Name == "Method")
        {
            var nameAttr = objectPath.Attributes?["Name"];
            if (nameAttr == null)
            {
                return null;
            }
            var methodName = actionType == ActionType.Query ? "query" : nameAttr.Value;
            return $"{typeName}.{methodName}";
        }

        if (objectPath.Name == "Property")
        {
            var nameAttr = objectPath.Attributes?["Name"];
            if (nameAttr == null)
            {
                return null;
            }
            if (typesDefinition?.ReturnTypes?.TryGetValue($"{typeName}.{nameAttr.Value}", out var returnType) == true)
            {
                var methodName = actionType == ActionType.SetProperty ? "setProperty" : nameAttr.Value;
                return $"{returnType}.{methodName}";
            }
            else
            {
                return $"{typeName}.{nameAttr.Value}";
            }
        }

        return null;
    }

    private static ActionType GetActionType(string actionName)
    {
        return actionName switch
        {
            "ObjectPath" => ActionType.ObjectPath,
            "Query" => ActionType.Query,
            "SetProperty" => ActionType.SetProperty,
            _ => throw new ArgumentOutOfRangeException(nameof(actionName), $"Unknown action type: {actionName}")
        };
    }
}