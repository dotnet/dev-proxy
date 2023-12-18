#!/usr/bin/env bash
#---------------------------------------------------------------------------------------------
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See License in the project root for license information.
#---------------------------------------------------------------------------------------------

mkdir devproxy
cd devproxy
full_path=$(pwd)

set -e # Terminates program immediately if any command below exits with a non-zero exit status

echo "Getting latest Dev Proxy version..."
version=$(curl -s https://api.github.com/repos/microsoft/dev-proxy/releases/latest | awk -F: '/"tag_name"/ {print $2}' | sed 's/[", ]//g')
echo "Latest version is $version"

echo "Downloading Dev Proxy $version..."

base_url="https://github.com/microsoft/dev-proxy/releases/download/$version/dev-proxy"

if [ "$(uname)" == "Darwin" ]; then
    ARCH="$(uname -m)"
    if [ ${ARCH} == "arm64" ]; then
        echo "unsupported architecture ${ARCH}. Aborting"; exit 1;
    elif [ ${ARCH} == "x86_64" ]; then
        curl -sL -o ./devproxy.zip "$base_url-osx-x64-$version.zip" || { echo "Cannot install Dev Proxy. Aborting"; exit 1; }
    else
        echo "unsupported architecture ${ARCH}"
        exit
    fi
elif [ "$(expr substr $(uname -s) 1 5)" == "Linux" ]; then
    ARCH="$(uname -m)"
    if [ "$(expr substr ${ARCH} 1 5)" == "arm64" ] || [ "$(expr substr ${ARCH} 1 7)" == "aarch64" ]; then
        echo "unsupported architecture ${ARCH}. Aborting"; exit 1;
    elif [ "$(expr substr ${ARCH} 1 6)" == "x86_64" ]; then
        curl -sL -o ./devproxy.zip "$base_url-linux-x64-$version.zip" || { echo "Cannot install Dev Proxy. Aborting"; exit 1; }
    else
        echo "unsupported architecture ${ARCH}"
        exit
    fi
fi

unzip -o ./devproxy.zip -d ./
rm ./devproxy.zip
chmod +x ./devproxy

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