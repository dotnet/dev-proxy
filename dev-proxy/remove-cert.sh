#!/bin/bash
set -e

if [ "$(uname -s)" != "Darwin" ]; then
  echo "Error: this shell script should be run on macOS."
  exit 1
fi

echo -e "\nRemove the self-signed certificate from your Keychain."

cert_name="Dev Proxy CA"
cert_filename="dev-proxy-ca.pem"

# export cert from keychain to PEM
echo "Exporting '$cert_name' certificate..."
security find-certificate -c "$cert_name" -a -p > "$cert_filename"

# add trusted cert to keychain
echo "Removing Dev Proxy trust settings..."
security remove-trusted-cert "$cert_filename"

# remove exported cert
echo "Cleaning up..."
rm "$cert_filename"
echo -e "\033[0;32mDONE\033[0m\n"