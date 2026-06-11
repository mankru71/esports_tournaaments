#!/bin/bash
dotnet tool install -g dotnet-ef
export PATH=$PATH:/root/.dotnet/tools
dotnet ef migrations add AddMatchChatAndNotifications
