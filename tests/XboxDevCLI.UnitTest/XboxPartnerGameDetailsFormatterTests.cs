// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using XboxDevCLI.Commands;
using XboxDevCLI.Output;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerGameDetailsFormatterTests
{
    [Fact]
    public void Format_NumbersMultipleProductsForTheSameTitle()
    {
        var result = XboxPartnerGameDetailsFormatter.Format(
            """
            {
              "games": [
                {
                  "productId": "abc",
                  "storeId": "9N0000000000",
                  "xboxTitleId": "12345678",
                  "SCID": "00000000-0000-0000-0000-000012345678",
                  "primaryDevelopmentSandbox": "TESTSB.42",
                  "name": "Nod Arena",
                  "packageIdentityName": "Nod.Arena",
                  "packageIdentityPublisher": "CN=Nod Publishers",
                  "packageFamilyName": "Nod.Arena_abc123",
                  "msaAppId": "0000000040ABCDEF"
                },
                {
                  "productId": "def",
                  "storeId": "9N0000000001",
                  "xboxTitleId": "12345678",
                  "name": "Nod Rally",
                  "packageIdentityName": "Nod.Rally",
                  "packageIdentityPublisher": "CN=Nod Publishers",
                  "packageFamilyName": "Nod.Rally_def456",
                  "msaAppId": "0000000040ABCDF0"
                }
              ]
            }
            """);

        Assert.Equal(2, result.ResultCount);
        Assert.Contains("TITLE ID: 12345678", result.Text, StringComparison.Ordinal);
        Assert.Contains("1. Nod Arena", result.Text, StringComparison.Ordinal);
        Assert.Contains("2. Nod Rally", result.Text, StringComparison.Ordinal);
        Assert.Contains("   Store ID: 9N0000000000", result.Text, StringComparison.Ordinal);
        Assert.Contains("   SCID: 00000000-0000-0000-0000-000012345678", result.Text, StringComparison.Ordinal);
        Assert.Contains("   Primary Development Sandbox: TESTSB.42", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            "   Package Identity Name: Nod.Arena",
            result.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "   Package Identity Publisher: CN=Nod Publishers",
            result.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "   Package Family Name: Nod.Arena_abc123",
            result.Text,
            StringComparison.Ordinal);
        Assert.Contains(
            "   MSA App ID: 0000000040ABCDEF",
            result.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OmitsNumberingForASingleProductAndKeepsDetailIndentation()
    {
        var result = XboxPartnerGameDetailsFormatter.Format(
            """
            {
              "games": [
                {
                  "productId": "abc",
                  "storeId": "9N0000000000",
                  "xboxTitleId": "12345678",
                  "SCID": null,
                  "primaryDevelopmentSandbox": null,
                  "name": "Nod Arena",
                  "packageIdentityName": null,
                  "packageIdentityPublisher": null,
                  "packageFamilyName": null,
                  "msaAppId": null
                }
              ]
            }
            """);

        Assert.DoesNotContain("1. Nod Arena", result.Text, StringComparison.Ordinal);
        Assert.Contains(
            $"{Environment.NewLine}Nod Arena{Environment.NewLine}   Store ID: 9N0000000000",
            result.Text,
            StringComparison.Ordinal);
        Assert.Contains("   Package Identity Name: -", result.Text, StringComparison.Ordinal);
        Assert.Contains("   MSA App ID: -", result.Text, StringComparison.Ordinal);
        Assert.Contains("   SCID: -", result.Text, StringComparison.Ordinal);
        Assert.Contains("   Primary Development Sandbox: -", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SanitizesValuesThatCouldBreakTheIndentedLayout()
    {
        var result = XboxPartnerGameDetailsFormatter.Format(
            """
            {
              "games": [
                {
                  "productId": "abc",
                  "storeId": "9N0000000000",
                  "xboxTitleId": "12345678",
                  "name": "Nod\r\nArena",
                  "packageIdentityName": "Nod\u2028Rally"
                }
              ]
            }
            """);

        Assert.Contains("Nod  Arena", result.Text, StringComparison.Ordinal);
        Assert.Contains("Nod Rally", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2028', result.Text);
    }

    [Fact]
    public void Format_ReportsNoResultsForAnEmptyCollection()
    {
        var result = XboxPartnerGameDetailsFormatter.Format("""{"games":[]}""");

        Assert.Equal(TableFormatter.EmptyNotice, result.Text);
        Assert.Equal(0, result.ResultCount);
    }

    [Fact]
    public void Format_KeepsGamesWithNullOrMissingTitleIdsSeparate()
    {
        var result = XboxPartnerGameDetailsFormatter.Format(
            """
            {"games":[
              {"name":"Nod Arena","xboxTitleId":null,"SCID":null,"primaryDevelopmentSandbox":null},
              {"name":"Lucerne Rally"}
            ]}
            """);

        Assert.Equal(2, result.ResultCount);
        Assert.Equal(2, result.Text.Split(Environment.NewLine).Count(line => line == "TITLE ID: -"));
        Assert.Contains("Nod Arena", result.Text, StringComparison.Ordinal);
        Assert.Contains("Lucerne Rally", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("1. ", result.Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Text.Split(Environment.NewLine).Count(line => line == "   SCID: -"));
    }
}
