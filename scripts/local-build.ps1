# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

$versionString = "v0.26.0-beta.1"
$version = $versionString.Substring(1)

Remove-Item ../bld -Recurse -Force

dotnet publish ../dev-proxy/dev-proxy.csproj -c Release -p:PublishSingleFile=true -r win-x64 --self-contained -o ../bld -p:InformationalVersion=$version
dotnet build ../dev-proxy-plugins/dev-proxy-plugins.csproj -c Release -r win-x64 --no-self-contained -p:InformationalVersion=$version
cp -R ../dev-proxy/bin/Release/net9.0/win-x64/plugins ../bld
pushd

cd ../bld
Get-ChildItem -Filter *.pdb -Recurse | Remove-Item
Get-ChildItem -Filter *.deps.json -Recurse | Remove-Item
Get-ChildItem -Filter *.runtimeconfig.json -Recurse | Remove-Item
popd