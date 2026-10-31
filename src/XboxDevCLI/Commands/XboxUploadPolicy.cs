// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI.Commands;

/// <summary>Local CLI preflight limits aligned with XboxPartner safety policy.</summary>
internal static class XboxUploadPolicy
{
    public const long MaxDocumentContentBytes = 50L * 1024 * 1024;
}
