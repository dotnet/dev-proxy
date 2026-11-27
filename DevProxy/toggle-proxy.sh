#!/bin/bash

toggle=$1
ip=$2
port=$3

# Skip the header line and disabled services (prefixed with *), which networksetup can't configure.
if ! all_services=$(networksetup -listallnetworkservices); then
  echo "Failed to list network services."
  exit 1
fi
network_services=$(echo "$all_services" | tail -n +2 | grep -v '^\*')

status=0

if [[ "$toggle" == "on" ]]; then
  while IFS= read -r service; do
    [[ -z "$service" ]] && continue
    networksetup -setsecurewebproxy "$service" "$ip" "$port" || status=1
    networksetup -setwebproxy "$service" "$ip" "$port" || status=1
  done <<<"$network_services"
elif [[ "$toggle" == "off" ]]; then
  while IFS= read -r service; do
    [[ -z "$service" ]] && continue
    networksetup -setsecurewebproxystate "$service" off || status=1
    networksetup -setwebproxystate "$service" off || status=1
  done <<<"$network_services"
else
  echo "Set the first argument to on or off."
  status=1
fi

exit $status
