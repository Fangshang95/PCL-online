[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

# PClonline

**Minecraft 連線啟動器 —— 讓開服連線像開房間一樣簡單**

一鍵開服 · 好友房間碼 · P2P 直連優先 / frp 中轉兜底

[![License](https://img.shields.io/badge/license-Apache--2.0-blue?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Fangshang95/PCL-online?style=for-the-badge&logo=github)](https://github.com/Fangshang95/PCL-online/releases)
[![Downloads](https://img.shields.io/github/downloads/Fangshang95/PCL-online/total?style=for-the-badge)](https://github.com/Fangshang95/PCL-online/releases)
[![Roadmap](https://img.shields.io/badge/roadmap-in%20progress-orange?style=for-the-badge)](#-路線圖)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Stars](https://img.shields.io/github/stars/Fangshang95/PCL-online?style=for-the-badge&color=eac54f)](https://github.com/Fangshang95/PCL-online)

</div>

PClonline 是基於 **PCL2-CE（Plain Craft Launcher 2 Community Edition）** 二次開發的 Minecraft 連線啟動器。

核心增值：**一鍵開服 + 好友房間碼加入 + P2P / frp 雙通道連線**——開服連線不再需要公網 IP、連接埠映射和命令列。

> ⚠️ 本專案為社群驅動的第三方啟動器，與 Mojang Studios、Microsoft 無任何關聯。

## ✨ 功能特性

在 PCL2-CE 全部功能（啟動器本體、模組管理、下載、多帳號等）之上，`PCL.MCStudio` 模組新增：

- **一鍵開服**：自動裝配伺服器端（vanilla / fabric 等）、依 MC 版本智慧選擇相容 Java（無本機相容 Java 時自動下載 Mojang 官方 runtime，雙來源校驗）
- **房間碼加入**：好友輸入 6 位房間碼即可加入，自動裝配同款整合包（marker 多鍵複用，不重複下載）
- **P2P 直連（優先）**：UDP 打洞（12 路並發打洞池、三段發包節奏、連接埠步進學習）+ PCP / UPnP / NATPMP 三協定連接埠映射並行競速 + KCP 資料通道
- **frp 中轉（兜底）**：NAT 不適合直連時自動回落 frp 隧道，房間級臨時憑證鑑權
- **帳號體系**：信箱註冊 / 好友清單 / 好友所開房間一覽
- **本機權杖 DPAPI 加密**：登入憑據整包加密落盤（CurrentUser 範圍）

## 🚀 快速上手

1. 從 [Releases](https://github.com/Fangshang95/PCL-online/releases) 下載 zip 並解壓縮（自包含版開箱即用）
2. 啟動 `Plain Craft Launcher 2.exe`，登入帳號（內測階段帳號由管理員分發，需要測試帳號請加 QQ：3215701517）
3. 在連線頁面建立房間，選擇遊戲版本或整合包，得到 6 位房間碼
4. 把房間碼發給好友——他們加入後自動裝配同款環境，直接開玩

## 📦 下載

見 [Releases](https://github.com/Fangshang95/PCL-online/releases)，兩個版本功能完全一致：

| 版本 | 體積 | 要求 |
|---|---|---|
| 自包含版（推薦） | ~83 MB | 無（內建 .NET 執行階段，解壓即用） |
| 框架依賴版 | ~16 MB | 需已安裝 [.NET Desktop Runtime 10](https://get.dot.net/10) |

## 🗺️ 路線圖

- [x] 一鍵開服 & 6 位房間碼加入
- [x] P2P 直連 + frp 中轉雙通道（自動切換）
- [x] 帳號 / 好友 / 房間清單體系
- [ ] 內測進行中：穩定性與體驗打磨
- [ ] frpc 執行階段自動下載
- [ ] 雲端 HTTPS
- [ ] 更多伺服器端類型（Paper / Forge 等）
- [ ] 整合包一鍵分享

## ⚠️ 連線服務說明

- P2P 信令、frp 中轉、帳號體系依賴 PClonline 官方雲端服務（自行部署，伺服器端程式碼暫不開源，本倉庫僅開源用戶端）
- 雲端位址等連線設定隨發佈包 `mcstudio/` 目錄下發，不進原始碼（原始碼內為佔位網域）
- 伺服器端採用「安全不靠隱藏」設計：雙層金鑰鑑權、房間級臨時憑證、信令歸屬校驗、多層限流——用戶端不持有任何高權限金鑰

## 🔨 從原始碼建置

需要 dotnet SDK 10.0+（需 `EnableWindowsTargeting`，倉庫已設定）：

```bash
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release          # 驗證編譯
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -o publish   # 自包含發佈
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fx   # 框架依賴發佈
```

支援 Windows x64 / ARM64。

## 📁 倉庫結構

```
Plain Craft Launcher 2/    主工程（WPF 主體 + 入口）
PCL.Core/                  PCL 社群核心庫
PCL.MCStudio/              ★ 連線模組（本專案核心增量）
PCL.MCStudio.SelfTest/     連線模組自測程式
```

## 🤝 參與貢獻

- 發現 Bug 或有功能建議：歡迎開 [Issue](https://github.com/Fangshang95/PCL-online/issues)（中文即可）
- 歡迎 PR：fork 後基於 `main` 分支開發，提交前請先跑通編譯
- 連線服務相關問題請附房間碼與時間點，便於排查

## 🔒 安全

發現安全問題請勿開公開 Issue，參見 [SECURITY.md](SECURITY.md)。

## 📄 授權條款

[Apache-2.0](LICENSE)，含 [NOTICE](NOTICE)（基於 PCL2-CE 與 PCL 的二次開發聲明）。

---

覺得好用就點個 ⭐ Star 吧——這是持續更新的最大動力

[下載最新版](https://github.com/Fangshang95/PCL-online/releases/latest) · [回報問題](https://github.com/Fangshang95/PCL-online/issues) · [查看路線圖](#-路線圖)