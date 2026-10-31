// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace XboxDevCLI.Services;

internal sealed record CreateXboxGameRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("deviceFamilies")] IReadOnlyCollection<XboxPartnerDeviceFamily> DeviceFamilies,
    [property: JsonPropertyName("sandboxId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SandboxId = null);

internal enum XboxPartnerDeviceFamily
{
    Xbox,
    Desktop,
}
