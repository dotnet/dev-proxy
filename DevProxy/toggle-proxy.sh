#!/bin/bash

toggle=$1
ip=$2
port=$3

network_services=$(networksetup -listallnetworkservices | tail -n +2)

if [[ "$toggle" == "on" ]]; then
  while IFS= read -r service; do
    networksetup -setsecurewebproxy "$service" $ip $port
    networksetup -setwebproxy "$service" $ip $port
  done <<<"$network_services"
elif [[ "$toggle" == "off" ]]; then
  while IFS= read -r service; do
    networksetup -setsecurewebproxystate "$service" off
    networksetup -setwebproxystate "$service" off
  done <<<"$network_services"
else
  echo "Set the first argument to on or off."
fi