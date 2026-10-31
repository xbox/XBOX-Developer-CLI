// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;

namespace PlayFabCodeGen;

/// <summary>
/// Parses Swagger 2.0 JSON spec files into an intermediate representation.
/// </summary>
public static class SwaggerParser
{
    /// <summary>
    /// Parses a Swagger 2.0 JSON spec into an <see cref="ApiGroup"/>.
    /// </summary>
    public static ApiGroup Parse(string groupName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var group = new ApiGroup { Name = groupName };

        // Parse paths → operations
        if (root.TryGetProperty("paths", out var paths))
        {
            foreach (var pathProp in paths.EnumerateObject())
            {
                var apiPath = pathProp.Name;
                foreach (var methodProp in pathProp.Value.EnumerateObject())
                {
                    // Skip non-operation keys (e.g. path-level "parameters") by
                    // filtering to known HTTP method names and object values.
                    if (methodProp.Value.ValueKind != JsonValueKind.Object ||
                        !IsHttpMethod(methodProp.Name))
                    {
                        continue;
                    }

                    var op = ParseOperation(apiPath, methodProp.Value);
                    if (op != null)
                    {
                        group.Operations.Add(op);
                    }
                }
            }
        }

        // Parse definitions → types
        if (root.TryGetProperty("definitions", out var defs))
        {
            foreach (var defProp in defs.EnumerateObject())
            {
                if (defProp.Name == "ApiErrorWrapper")
                {
                    continue;
                }

                var typeDef = ParseDefinition(defProp.Name, defProp.Value);
                group.Types.Add(typeDef);
            }
        }

        return group;
    }

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "options", "head"
    };

    private static bool IsHttpMethod(string name) => HttpMethods.Contains(name);

    private static ApiOperation? ParseOperation(string apiPath, JsonElement opElement)
    {
        var op = new ApiOperation { Path = apiPath };

        if (opElement.TryGetProperty("operationId", out var opId))
        {
            op.OperationId = opId.GetString() ?? "";
        }

        if (opElement.TryGetProperty("description", out var desc))
        {
            op.Description = desc.GetString() ?? "";
        }

        if (opElement.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0)
        {
            op.Tag = tags[0].GetString() ?? "";
        }

        // Extract request type from parameters
        if (opElement.TryGetProperty("parameters", out var parameters))
        {
            foreach (var param in parameters.EnumerateArray())
            {
                if (param.TryGetProperty("$ref", out var paramRef))
                {
                    // e.g. "#/parameters/GetLanguageListRequest"
                    op.RequestTypeName = ExtractRefName(paramRef.GetString());
                }
                else if (param.TryGetProperty("schema", out var schema) &&
                         schema.TryGetProperty("$ref", out var schemaRef))
                {
                    op.RequestTypeName = ExtractRefName(schemaRef.GetString());
                }
            }
        }

        // Extract response type from 200 response
        if (opElement.TryGetProperty("responses", out var responses) &&
            responses.TryGetProperty("200", out var okResponse))
        {
            if (okResponse.TryGetProperty("$ref", out var respRef))
            {
                // e.g. "#/responses/GetLanguageListResponse"
                op.ResponseTypeName = ExtractRefName(respRef.GetString());
            }
            else if (okResponse.TryGetProperty("schema", out var schema) &&
                     schema.TryGetProperty("$ref", out var schemaRef))
            {
                op.ResponseTypeName = ExtractRefName(schemaRef.GetString());
            }
        }

        return op;
    }

    private static TypeDefinition ParseDefinition(string name, JsonElement defElement)
    {
        var typeDef = new TypeDefinition { Name = name };

        if (defElement.TryGetProperty("description", out var desc))
        {
            typeDef.Description = desc.GetString() ?? "";
        }

        // Check if this is an enum
        if (defElement.TryGetProperty("type", out var typeEl) &&
            typeEl.GetString() == "string" &&
            defElement.TryGetProperty("enum", out var enumValues))
        {
            typeDef.IsEnum = true;
            foreach (var enumVal in enumValues.EnumerateArray())
            {
                typeDef.EnumValues.Add(enumVal.GetString() ?? "");
            }

            return typeDef;
        }

        // Parse required fields
        var requiredFields = new HashSet<string>();
        if (defElement.TryGetProperty("required", out var required))
        {
            foreach (var req in required.EnumerateArray())
            {
                requiredFields.Add(req.GetString() ?? "");
            }
        }

        // Parse properties
        if (defElement.TryGetProperty("properties", out var props))
        {
            foreach (var propEl in props.EnumerateObject())
            {
                var prop = ParseProperty(propEl.Name, propEl.Value);
                prop.IsRequired = requiredFields.Contains(propEl.Name);
                typeDef.Properties.Add(prop);
            }
        }

        return typeDef;
    }

    private static PropertyDefinition ParseProperty(string name, JsonElement propElement)
    {
        var prop = new PropertyDefinition { Name = name };

        if (propElement.TryGetProperty("description", out var desc))
        {
            prop.Description = desc.GetString() ?? "";
        }

        // Determine the type
        if (propElement.TryGetProperty("$ref", out var refEl))
        {
            prop.TypeRef = ExtractRefName(refEl.GetString());
        }
        else if (propElement.TryGetProperty("type", out var typeEl))
        {
            var typeStr = typeEl.GetString() ?? "object";

            switch (typeStr)
            {
                case "array":
                    prop.IsArray = true;
                    if (propElement.TryGetProperty("items", out var items))
                    {
                        if (items.TryGetProperty("$ref", out var itemRef))
                        {
                            prop.TypeRef = ExtractRefName(itemRef.GetString());
                        }
                        else if (items.TryGetProperty("type", out var itemType))
                        {
                            prop.PrimitiveType = itemType.GetString() ?? "string";

                            if (items.TryGetProperty("format", out var itemFormat))
                            {
                                prop.Format = itemFormat.GetString();
                            }
                        }
                    }

                    break;

                case "object":
                    // object with additionalProperties = Dictionary
                    if (propElement.TryGetProperty("additionalProperties", out var addProps))
                    {
                        prop.IsDictionary = true;
                        if (addProps.TryGetProperty("$ref", out var valRef))
                        {
                            prop.TypeRef = ExtractRefName(valRef.GetString());
                        }
                        else if (addProps.TryGetProperty("type", out var valType))
                        {
                            prop.PrimitiveType = valType.GetString() ?? "string";
                        }
                    }
                    else
                    {
                        // Bare "object" with no additionalProperties = treat as Dictionary<string, object>
                        prop.IsDictionary = true;
                        prop.PrimitiveType = "object";
                    }

                    break;

                default:
                    prop.PrimitiveType = typeStr;
                    if (propElement.TryGetProperty("format", out var format))
                    {
                        prop.Format = format.GetString();
                    }

                    break;
            }
        }

        return prop;
    }

    private static string? ExtractRefName(string? refPath)
    {
        if (refPath == null)
        {
            return null;
        }

        // "#/definitions/EntityKey" → "EntityKey"
        // "#/parameters/GetLanguageListRequest" → "GetLanguageListRequest"
        // "#/responses/GetLanguageListResponse" → "GetLanguageListResponse"
        var lastSlash = refPath.LastIndexOf('/');
        return lastSlash >= 0 ? refPath[(lastSlash + 1)..] : refPath;
    }
}

// ---- Intermediate representation ----

public sealed class ApiGroup
{
    public string Name { get; set; } = "";
    public List<ApiOperation> Operations { get; set; } = [];
    public List<TypeDefinition> Types { get; set; } = [];
}

public sealed class ApiOperation
{
    public string Path { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tag { get; set; } = "";
    public string? RequestTypeName { get; set; }
    public string? ResponseTypeName { get; set; }
}

public sealed class TypeDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEnum { get; set; }
    public List<string> EnumValues { get; set; } = [];
    public List<PropertyDefinition> Properties { get; set; } = [];
}

public sealed class PropertyDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsRequired { get; set; }
    public bool IsArray { get; set; }
    public bool IsDictionary { get; set; }

    /// <summary>Reference to another definition (complex type or enum).</summary>
    public string? TypeRef { get; set; }

    /// <summary>Primitive type name: string, integer, number, boolean.</summary>
    public string? PrimitiveType { get; set; }

    /// <summary>Format hint: int32, int64, double, date-time, etc.</summary>
    public string? Format { get; set; }
}
