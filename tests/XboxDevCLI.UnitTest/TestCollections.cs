// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.UnitTest;

/// <summary>
/// Tests in this collection redirect Console.Out/Error and must not run
/// in parallel with each other or other tests that use the console.
/// </summary>
[CollectionDefinition("ConsoleRedirect")]
public class ConsoleRedirectCollection;
