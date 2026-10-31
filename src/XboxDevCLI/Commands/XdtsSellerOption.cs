// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using XboxDevCLI.Services;

namespace XboxDevCLI.Commands;

internal sealed class XdtsSellerOption
{
    private readonly Option<long?> _sellerIdOption = CreatePositiveLongOption(
        "--seller-id",
        "Optional XBOX seller account ID. Must be authorized for the signed-in developer.")
        .AsContextValue(ContextEndpoint.XboxPartner);

    public void AddTo(Command command) => command.Options.Add(_sellerIdOption);

    /// <summary>
    /// The seller: <c>--seller-id</c>, which <see cref="ContextResolver"/> fills in from
    /// <c>XBOX_SELLER_ID</c> or saved context, or <c>null</c>, for the developer's only seller.
    /// The other parameters are the ones the XBOX Live commands pass; reading it needs none.
    /// </summary>
    public Task<long?> GetSellerIdAsync(
        ParseResult parseResult,
        IConfiguration configuration,
        IXboxPartnerClient client,
        string baseUrl,
        CancellationToken cancellationToken) =>
        Task.FromResult(parseResult.GetValue(_sellerIdOption));

    private static Option<long?> CreatePositiveLongOption(string name, string description)
    {
        var option = new Option<long?>(name)
        {
            Description = description,
        };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0
                && long.TryParse(result.Tokens[0].Value, out long sellerId)
                && sellerId <= 0)
            {
                result.AddError($"{name} must be a positive numeric value.");
            }
        });
        return option;
    }
}
