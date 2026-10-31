// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

[Collection("ConsoleRedirect")]
public class AuditLoggerTests
{
    [Fact]
    public void WriteInvocation_UnwritablePath_WarnsWithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLog:Enabled"] = bool.TrueString,
            })
            .Build();
        var originalError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            using var logger = new AuditLogger(configuration, "ABC123", "\0");
            logger.WriteInvocation("/Admin/SetTitleData", 0);

            Assert.Contains("Warning: could not write the audit log:", error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
