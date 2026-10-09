[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

# PClonline

**A Minecraft multiplayer launcher — hosting a server is as easy as creating a room**

One-click hosting · Friend room codes · P2P-first / frp relay fallback

[![License](https://img.shields.io/badge/license-Apache--2.0-blue?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Fangshang95/PCL-online?style=for-the-badge&logo=github)](https://github.com/Fangshang95/PCL-online/releases)
[![Downloads](https://img.shields.io/github/downloads/Fangshang95/PCL-online/total?style=for-the-badge)](https://github.com/Fangshang95/PCL-online/releases)
[![Roadmap](https://img.shields.io/badge/roadmap-in%20progress-orange?style=for-the-badge)](#-roadmap)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Stars](https://img.shields.io/github/stars/Fangshang95/PCL-online?style=for-the-badge&color=eac54f)](https://github.com/Fangshang95/PCL-online)

</div>

PClonline is a Minecraft multiplayer launcher built on top of **PCL2-CE (Plain Craft Launcher 2 Community Edition)**.

Core value: **one-click server hosting + joining by friend room code + P2P / frp dual-channel networking** — no public IP, no port forwarding, no command line required.

> ⚠️ This is a community-driven third-party launcher and is not affiliated with Mojang Studios or Microsoft in any way.

## ✨ Features

On top of everything PCL2-CE already offers (launcher core, mod management, downloads, multi-account support, etc.), the `PCL.MCStudio` module adds:

- **One-click hosting**: automatically provisions the server (vanilla / fabric, etc.) and picks a compatible Java for the MC version (downloads Mojang's official runtime with dual-source verification when no local compatible Java is found)
- **Join by room code**: friends enter a 6-digit room code to join, and the same modpack is provisioned automatically (multi-key marker reuse, no duplicate downloads)
- **P2P direct connection (preferred)**: UDP hole punching (12-way concurrent punch pool, three-stage packet cadence, port stepping learning) + PCP / UPnP / NATPMP port mapping racing in parallel + KCP data channel
- **frp relay (fallback)**: automatically falls back to an frp tunnel when the NAT is unsuitable for direct connections, authenticated with room-scoped temporary credentials
- **Account system**: email registration / friend list / list of rooms opened by friends
- **Local token DPAPI encryption**: login credentials are encrypted as a whole on disk (CurrentUser scope)

## 🚀 Quick Start

1. Download the zip from [Releases](https://github.com/Fangshang95/PCL-online/releases) and extract it (the self-contained build works out of the box)
2. Launch `Plain Craft Launcher 2.exe` and sign in (during the beta, accounts are distributed by admins — for a test account, add QQ: 3215701517)
3. On the multiplayer page, create a room, pick a game version or modpack, and get a 6-digit room code
4. Send the room code to your friends — they join, get the same environment provisioned automatically, and can play right away

## 📦 Downloads

See [Releases](https://github.com/Fangshang95/PCL-online/releases). Both builds are functionally identical:

| Build | Size | Requirement |
|---|---|---|
| Self-contained (recommended) | ~83 MB | None (bundles the .NET runtime, extract and run) |
| Framework-dependent | ~16 MB | [.NET Desktop Runtime 10](https://get.dot.net/10) must be installed |

## 🗺️ Roadmap

- [x] One-click hosting & 6-digit room code joining
- [x] P2P direct + frp relay dual channel (auto switching)
- [x] Account / friends / room list system
- [ ] Beta in progress: stability and experience polish
- [ ] Automatic frpc runtime download
- [ ] Cloud HTTPS
- [ ] More server types (Paper / Forge, etc.)
- [ ] One-click modpack sharing

## ⚠️ Multiplayer Service Notes

- P2P signaling, frp relay and the account system rely on PClonline's official cloud service (self-hosted; the server code is not open source for now — this repository contains the client only)
- Connection configuration such as the cloud address is shipped inside the release package's `mcstudio/` directory and never enters the source tree (the source contains placeholder domains)
- The server follows a "security does not rely on obscurity" design: two-layer key authentication, room-scoped temporary credentials, signaling ownership validation, and multi-layer rate limiting — the client holds no high-privilege secrets

## 🔨 Building from Source

Requires dotnet SDK 10.0+ (with `EnableWindowsTargeting`, already configured in the repository):

```bash
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release          # verify compilation
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -o publish   # self-contained publish
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fx   # framework-dependent publish
```

Supports Windows x64 / ARM64.

## 📁 Repository Layout

```
Plain Craft Launcher 2/    Main project (WPF shell + entry point)
PCL.Core/                  PCL community core library
PCL.MCStudio/              ★ Multiplayer module (the core addition of this project)
PCL.MCStudio.SelfTest/     Self-test program for the multiplayer module
```

## 🤝 Contributing

- Found a bug or have a feature request? Feel free to open an [Issue](https://github.com/Fangshang95/PCL-online/issues) (Chinese is fine)
- PRs are welcome: fork and work on the `main` branch, and make sure it compiles before submitting
- For multiplayer service issues, please include the room code and timestamp to help us investigate

## 🔒 Security

Please do not open a public Issue for security problems — see [SECURITY.md](SECURITY.md).

## 📄 License

[Apache-2.0](LICENSE), with a [NOTICE](NOTICE) (attribution for the derivative work based on PCL2-CE and PCL).

---

If you find it useful, give it a ⭐ Star — it's the biggest motivation to keep improving

[Download latest](https://github.com/Fangshang95/PCL-online/releases/latest) · [Report an issue](https://github.com/Fangshang95/PCL-online/issues) · [View roadmap](#-roadmap)