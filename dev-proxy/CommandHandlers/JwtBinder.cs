// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DevProxy.Jwt;
using System.CommandLine;
using System.CommandLine.Binding;

namespace Microsoft.DevProxy.CommandHandlers
{
    public class JwtBinder(Option<string> nameOption, Option<IEnumerable<string>> audiencesOption, Option<string> issuerOption, Option<IEnumerable<string>> rolesOption, Option<IEnumerable<string>> scopesOption, Option<Dictionary<string, string>> claimsOption, Option<double> validForOption, Option<string> signingKeyOption) : BinderBase<JwtOptions>
    {
        private readonly Option<string> _nameOption = nameOption;
        private readonly Option<IEnumerable<string>> _audiencesOption = audiencesOption;
        private readonly Option<string> _issuerOption = issuerOption;
        private readonly Option<IEnumerable<string>> _rolesOption = rolesOption;
        private readonly Option<IEnumerable<string>> _scopesOption = scopesOption;
        private readonly Option<Dictionary<string, string>> _claimsOption = claimsOption;
        private readonly Option<double> _validForOption = validForOption;
        private readonly Option<string> _signingKeyOption = signingKeyOption;

        protected override JwtOptions GetBoundValue(BindingContext bindingContext)
        {
            return new JwtOptions
            {
                Name = bindingContext.ParseResult.GetValueForOption(_nameOption),
                Audiences = bindingContext.ParseResult.GetValueForOption(_audiencesOption),
                Issuer = bindingContext.ParseResult.GetValueForOption(_issuerOption),
                Roles = bindingContext.ParseResult.GetValueForOption(_rolesOption),
                Scopes = bindingContext.ParseResult.GetValueForOption(_scopesOption),
                Claims = bindingContext.ParseResult.GetValueForOption(_claimsOption),
                ValidFor = bindingContext.ParseResult.GetValueForOption(_validForOption),
                SigningKey = bindingContext.ParseResult.GetValueForOption(_signingKeyOption)
            };
        }
    }
}
