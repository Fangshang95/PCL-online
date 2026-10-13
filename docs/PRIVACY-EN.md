# PClonline Privacy Policy

Last updated: 2026-10-13

PClonline ("this launcher") is an open-source Minecraft launcher based on PCL2-CE, providing player-hosted P2P multiplayer. This policy explains what we collect, why, where it is stored, and what rights you have.

The launcher is maintained by an individual developer. The server is located in mainland China. **We do not sell user data and we show no advertisements.**

---

## 1. Information we collect

### Account
- **Email address** — used as the unique account identifier.
- **Nickname** — up to 7 characters, **used only for display in your friends list**. It is not used for lookup and is not required to be unique.
- **Password** — never stored in plaintext. Passwords are hashed with scrypt (n=2^14, r=8, p=1) using a per-account random salt. Nobody, including the operator, can recover your password from the database.
- **How your password travels** — from **v50.12.0** onward, registration, login, friends and multiplayer signaling reach our server over **TLS**. The certificate is issued by our own root and pinned to the server's IP address; the client trusts only that root and does not rely on the system trust store. **If you are on an earlier version, your login password still crosses the network as unencrypted HTTP**, and anyone on the same network (e.g. a public Wi-Fi hotspot) may be able to capture it. Please update.

### Sessions
- A random session token is issued on login; only its SHA-256 hash is stored, valid for 30 days.
- A new login invalidates that account's previous sessions (single active device).
- The token is stored on your machine encrypted with the Windows Data Protection API (DPAPI, current-user scope).

### Device identifier (anti mass-registration)
- At registration the launcher collects several hardware identifiers on your machine (SMBIOS system UUID, baseboard product and serial, processor ID; falling back to the OS installation identifier), and reduces them to a one-way digest **locally** before sending it.
- **The server stores neither the raw hardware identifiers nor that digest** — only an HMAC-SHA256 of the digest keyed by a server-side secret. Even a full database leak cannot be reversed to any hardware information and cannot be matched back to your device.
- It is used solely to limit mass account registration from one machine. **Login is never restricted by device**: another computer, a reinstalled system, or an internet café will not be refused.
- If hardware identifiers cannot be read reliably, the check is skipped and registration proceeds normally. We never lock a real person out because of a collection failure.

### Multiplayer telemetry
To operate and debug P2P connectivity, the server records:
- your **IP address** (rate limiting, abuse prevention, and address echo);
- **hole-punching diagnostics**: NAT type, candidate ports, success or failure, room code, client version. These contain no game content and no chat.
- **Room information**: room code, host, member list, Minecraft server address and MOTD.

### Automatic updates
Checking for updates contacts **our server first**, which serves a small (~16 KB) update manifest; GitHub is only used as a fallback. The update package itself is always downloaded from GitHub Releases and never passes through our server. As a result:

- our server sees your **IP address, the manifest requested and the time of the request**;
- when falling back to GitHub, GitHub sees your IP address and the client version being downloaded.

The manifest is ECDSA-signed, so a compromised server cannot substitute a forged one. We do not use this request for advertising, analytics or profiling.

### Server access logs
Our server writes one access-log line per API request, containing the **source IP address, the request path and the response status**, into the system journal. It is used solely for rate-limit triage and abuse investigation, is retained under the default journal policy (up to roughly three months) and is not repurposed.

---

## 2. What other players can see — please read

**Inside a room, other players can see the email account you signed in with, your nickname, and your online status.** This is required to establish the P2P connection relationship and cannot be avoided.

> ⚠ **Do not register with your primary personal mailbox.** We recommend a dedicated address used only for multiplayer.

Friend relationships exist only after you explicitly add someone, which requires knowing their email account.

---

## 3. What we do not do

- **No third-party analytics, advertising, or crash reporting.** The upstream Sentry telemetry of PCL2-CE is fully disabled in this fork; errors are written to your local log files only.
- We do not read or upload your game files, worlds, chat, or game account credentials. Minecraft account tokens are used only against Microsoft/Mojang official endpoints and are never stored on our server.
- We do not request contacts, location, microphone, or camera permissions.
- We do not sell or share your personal information with any third party. (During P2P hole punching your IP is directly visible to other players in the same room; this is inherent to the technology and is not a disclosure to a third-party service.)

---

## 4. Retention and deletion

- Account, session, friend, device-binding and room data live on our own servers in mainland China.
- **You can delete everything yourself**: sign in under Multiplayer → Account and use account deletion (password confirmation). Deletion immediately removes your user record, all sessions, friend relationships, device bindings, and any room hosted by that account. This cannot be undone.
- Session tokens expire after 30 days; hole-punching diagnostics are rotated out over time.

---

## 5. Minors

Minecraft players may include minors. If you are a minor, please read this policy with a guardian and use the launcher only with their consent. We do not knowingly collect identity information from minors; a guardian may request account deletion through the contact below.

---

## 6. Changes

Material changes (for example a new category of collected data) are announced with a client update and by revising the date above. Continuing to use the launcher means you accept the updated policy.

---

## 7. Contact

- Issues and deletion requests: <https://github.com/Fangshang95/PCL-online/issues>
- Developer email: <!-- TODO: fill in a public contact address -->
