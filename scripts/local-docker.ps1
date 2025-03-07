# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

. (Join-Path $PSScriptRoot "version.ps1")
. (Join-Path $PSScriptRoot "local-build.ps1")

$version = $versionString.Substring(1)

docker build --build-arg DEVPROXY_VERSION=$version -t dev-proxy-local:$version -f Dockerfile_local ..