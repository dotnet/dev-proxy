#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

if [ "$(expr substr $(uname -s) 1 5)" != "Linux" ]; then
    echo "Unsupported OS. This script is for installing Dev Proxy on Linux. To install Dev Proxy on macOS or Windows use their installers. For more information, visit https://aka.ms/devproxy/start."
    exit 1
fi

echo ""
echo "This script installs Dev Proxy on your machine. It runs the following steps:"
echo ""
echo "1. Create the 'devproxy' directory in the current working folder"
echo "2. Download the latest Dev Proxy release"
echo "3. Unzip the release in the devproxy directory"
echo "4. Configure devproxy and its files as executable"
echo "5. Add the devproxy directory to your PATH environment variable in your shell profile"
echo ""

if [ -t 0 ]; then
    # Terminal is interactive, prompt the user
    read -p "Continue (y/n)? " -n1 -r response
else
    # Not interactive, set a default response
    response='y'
fi

if [[ "$response" != [yY] ]]; then
    echo -e "\nExiting"
    exit 1
fi

if [ -t 0 ]; then
    echo -e "\n"
fi

mkdir devproxy
cd devproxy
full_path=$(pwd)

set -e # Terminates program immediately if any command below exits with a non-zero exit status

if [ $# -eq 0 ]
then
    echo "Getting latest Dev Proxy version..."
    version=$(curl -s https://api.github.com/repos/dotnet/dev-proxy/releases/latest | awk -F: '/"tag_name"/ {print $2}' | sed 's/[", ]//g')
    echo "Latest version is $version"
else
    version=$1
fi

echo "Downloading Dev Proxy $version..."

base_url="https://github.com/dotnet/dev-proxy/releases/download/$version/dev-proxy"

ARCH="$(uname -m)"
if [ "$(expr substr ${ARCH} 1 5)" == "arm64" ] || [ "$(expr substr ${ARCH} 1 7)" == "aarch64" ]; then
    curl -sL -o ./devproxy.zip "$base_url-linux-arm64-$version.zip" || { echo "Cannot install Dev Proxy. Aborting"; exit 1; }
elif [ "$(expr substr ${ARCH} 1 6)" == "x86_64" ]; then
    curl -sL -o ./devproxy.zip "$base_url-linux-x64-$version.zip" || { echo "Cannot install Dev Proxy. Aborting"; exit 1; }
else
    echo "unsupported architecture ${ARCH}"
    exit
fi

unzip -o ./devproxy.zip -d ./
rm ./devproxy.zip
echo "Configuring devproxy and its files as executable..."
chmod +x ./devproxy ./libe_sqlite3.so

echo "Adding devproxy to the PATH environment variable in your shell profile..."

if [[ ":$PATH:" != *":$full_path:"* ]]; then
    if [[ -e ~/.zshrc ]]; then
        echo -e "\n# Dev Proxy\nexport PATH=\$PATH:$full_path" >> $HOME/.zshrc
        fileUsed="~/.zshrc"
    elif  [[ -e ~/.bashrc ]]; then
        echo -e "\n# Dev Proxy\nexport PATH=\$PATH:$full_path" >> $HOME/.bashrc 
        fileUsed="~/.bashrc"
    else
        echo -e "\n# Dev Proxy\nexport PATH=\$PATH:$full_path" >> $HOME/.bash_profile
        fileUsed="~/.bash_profile"
    fi
fi

echo "Dev Proxy $version installed!"
echo ""
echo "To get started, run:"
if [[ "$fileUsed" != "" ]]; then
    echo "    source $fileUsed"
fi
echo "    devproxy -h"
