**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

# PClonline

**Minecraft 联机启动器 —— 让开服联机像建房间一样简单**

一键开服 · 好友房间码 · P2P 直连优先 / frp 中转兜底

[![License](https://img.shields.io/badge/license-Apache--2.0-blue?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Fangshang95/PCL-online?style=for-the-badge&logo=github)](https://github.com/Fangshang95/PCL-online/releases)
[![Downloads](https://img.shields.io/github/downloads/Fangshang95/PCL-online/total?style=for-the-badge)](https://github.com/Fangshang95/PCL-online/releases)
[![Roadmap](https://img.shields.io/badge/roadmap-in%20progress-orange?style=for-the-badge)](#-路线图)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Stars](https://img.shields.io/github/stars/Fangshang95/PCL-online?style=for-the-badge&color=eac54f)](https://github.com/Fangshang95/PCL-online)

</div>

**English summary**: A Minecraft launcher built on PCL2-CE, featuring one-click server hosting and room-code multiplayer (P2P-first with FRP relay fallback).

基于 **PCL2-CE（Plain Craft Launcher 2 Community Edition）** 二次开发的 Minecraft 联机启动器。

核心增值：**一键开服 + 好友房间码加入 + P2P / frp 双通道联机**——开服联机不再需要公网 IP、端口映射和命令行。

> ⚠️ 本项目为社区驱动的第三方启动器，与 Mojang Studios、Microsoft 无任何关联。

## ✨ 功能特性

在 PCL2-CE 全部功能（启动器本体、模组管理、下载、多账户等）之上，`PCL.MCStudio` 模块新增：

- **一键开服**：自动装配服务端（vanilla / fabric 等）、按 MC 版本智能选择兼容 Java（无本地兼容 Java 时自动下载 Mojang 官方 runtime，双源校验）
- **房间码加入**：好友输入 6 位房间码即可加入，自动装配同款整合包（marker 多键复用，不重复下载）
- **P2P 直连（优先）**：UDP 打洞（12 路并发打洞池、三段发包节奏、端口步进学习）+ PCP / UPnP / NATPMP 三协议端口映射并行竞速 + KCP 数据通道
- **frp 中转（兜底）**：NAT 不适合直连时自动回落 frp 隧道，房间级临时凭证鉴权
- **账号体系**：邮箱注册 / 好友列表 / 好友所开房间一览
- **本地令牌 DPAPI 加密**：登录凭据整包加密落盘（CurrentUser 范围）

## 🚀 快速上手

1. 从 [Releases](https://github.com/Fangshang95/PCL-online/releases) 下载 zip 并解压（自包含版开箱即用）
2. 启动 `Plain Craft Launcher 2.exe`，登录账号（内测阶段账户由管理员分发，需要测试账号请加 QQ：3215701517）
3. 在联机页面创建房间，选择游戏版本或整合包，得到 6 位房间码
4. 把房间码发给好友——他们加入后自动装配同款环境，直接开玩

## 📦 下载

见 [Releases](https://github.com/Fangshang95/PCL-online/releases)，两个版本功能完全一致：

| 版本 | 体积 | 要求 |
|---|---|---|
| 自包含版（推荐） | ~83 MB | 无（内置 .NET 运行时，解压即用） |
| 框架依赖版 | ~16 MB | 需已安装 [.NET Desktop Runtime 10](https://get.dot.net/10) |

## 🗺️ 路线图

- [x] 一键开服 & 6 位房间码加入
- [x] P2P 直连 + frp 中转双通道（自动切换）
- [x] 账号 / 好友 / 房间列表体系
- [ ] 内测进行中：稳定性与体验打磨
- [ ] frpc 运行时自动下载
- [ ] 云端 HTTPS
- [ ] 更多服务端类型（Paper / Forge 等）
- [ ] 整合包一键分享

## ⚠️ 联机服务说明

- P2P 信令、frp 中转、账号体系依赖 PClonline 官方云端服务（自部署，服务端代码暂不开源，本仓库仅开源客户端）
- 云端地址等连接配置随发布包 `mcstudio/` 目录下发，不进源码（源码内为占位域名）
- 服务端采用「安全不靠隐藏」设计：双层密钥鉴权、房间级临时凭证、信令归属校验、多层限流——客户端侧不持有任何高权限密钥

## 🔨 从源码构建

需要 dotnet SDK 10.0+（需 `EnableWindowsTargeting`，仓库已配置）：

```bash
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release          # 验证编译
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -o publish   # 自包含发布
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fx   # 框架依赖发布
```

支持 Windows x64 / ARM64。

## 📁 仓库结构

```
Plain Craft Launcher 2/    主工程（WPF 主体 + 入口）
PCL.Core/                  PCL 社区核心库
PCL.MCStudio/              ★ 联机模块（本项目核心增量）
PCL.MCStudio.SelfTest/     联机模块自测程序
```

## 🤝 参与贡献

- 发现 Bug 或有功能建议：欢迎开 [Issue](https://github.com/Fangshang95/PCL-online/issues)（中文即可）
- 欢迎 PR：fork 后基于 `main` 分支开发，提交前请先跑通编译
- 联机服务相关问题请附房间码与时间点，便于排查

## 🔒 安全

发现安全问题请勿开公开 Issue，参见 [SECURITY.md](SECURITY.md)。

## 📄 许可证

[Apache-2.0](LICENSE)，含 [NOTICE](NOTICE)（基于 PCL2-CE 与 PCL 的二次开发声明）。

---

觉得好用就点个 ⭐ Star 吧——这是持续更新的最大动力

[下载最新版](https://github.com/Fangshang95/PCL-online/releases/latest) · [报告问题](https://github.com/Fangshang95/PCL-online/issues) · [查看路线图](#-路线图)