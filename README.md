# glacc-auto

自动领取给梨加速器时长，每天领取315分钟。

---

> ### ⚠️ 请先阅读
> 本项目**仅供个人学习、研究与技术交流**，**严禁任何商业或盈利性使用**。
> 本软件为非官方第三方工具，与任何第三方服务提供方**无任何关联**。
> 使用可能带来的账号限制、权益损失及法律风险**由使用者自行承担**。
> 完整条款见 **[LICENSE](LICENSE)** 与 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

## 软件截图

<img src=".\static\screenshot\img_1.png" alt="img_1" style="zoom:50%;" />

## 功能特性

- **一键领取** —— 登录后点一次按钮，自动跑完当日全部任务，无需人工干预
- **余额一览** —— 首页直接显示当前可用时长（时 / 分）
- **定时领取** —— Windows 计划任务 / Linux systemd 用户定时器（或 crontab）每天指定时间自动执行
- **结果通知** —— 可选接入 Server酱，定时领取结束后把结果推送到微信
- **原生观感** —— Win11 Fluent 风格界面，支持跟随系统深浅色与界面缩放
- **Linux CLI** —— 无界面命令行：`login` / `claim` / `schedule`，适合服务器与常开 Linux 主机

## Windows 下载

前往 **[Releases](https://github.com/JiangXu26710/glacc-auto/releases/latest)** 下载最新的 `glacc-auto-v*.zip`，解压到任意目录，双击 `GlaccAuto.Gui.exe` 即可运行。

- **系统要求**：Windows 10 / 11，64 位

## Linux 安装（每日自动领取）

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。x86_64 与 aarch64 均可。

```bash
git clone https://github.com/JiangXu26710/glacc-auto.git
cd glacc-auto
chmod +x scripts/install-linux.sh
./scripts/install-linux.sh
```

默认安装到 `~/.local/share/glacc-auto/`，并在 `~/.local/bin/glacc-auto` 放软链接。确保 `~/.local/bin` 在 `PATH` 中。

```bash
# 短信登录（交互输入手机号与验证码）
glacc-auto login

# 立刻领取一次
glacc-auto claim

# 查看余额与今日进度
glacc-auto status

# 每天 08:00 自动领取（优先注册 systemd --user timer，否则写入 crontab）
glacc-auto schedule on 08:00

# 关闭定时
glacc-auto schedule off
```

安装时也可直接带上时刻：

```bash
./scripts/install-linux.sh --time 08:00
```

### systemd 用户定时器说明

- 单元名：`glacc-auto-claim.timer` / `glacc-auto-claim.service`
- `Persistent=true`：错过的触发会在下次开机补跑
- 若希望**未登录也到点执行**（服务器、无桌面）：

  ```bash
  sudo loginctl enable-linger "$USER"
  ```

- 查看下次触发：`systemctl --user list-timers glacc-auto-claim.timer`
- 手动触发一次：`systemctl --user start glacc-auto-claim.service`

无 systemd 的环境会把一行 crontab 写入当前用户（带 `# glacc-auto-claim` 标记）。

### 数据目录

| 平台 | 默认路径 |
| --- | --- |
| Windows | `%APPDATA%\glacc-auto\` |
| Linux | `${XDG_CONFIG_HOME:-$HOME/.config}/glacc-auto/` |

可用环境变量 `GLACC_HOME` 覆盖。凭证、设置、日志都在此目录，不会上传。

在 `settings.json` 里可配置：

- `ScheduledTime`：`HH:mm`（默认 `08:00`）
- `ServerKey`：Server酱 SendKey（`SCT...`），定时领取结束后推送微信
- `IntervalMinSec` / `IntervalMaxSec`：每次推送间隔随机区间（默认 30–40 秒）
- `NetworkRetryCount`：网络失败额外重试次数（默认 3）

## 常见问题

**定时领取未生效？（Windows）**

- 确保设置页的开关处于开启状态，并检查 Windows 任务计划程序中是否存在 `glacc-auto-claim` 任务
- 确保到达预定时间后，电脑处于开机已登录状态
- 确保杀毒软件不会误报、拦截本软件

**定时领取未生效？（Linux）**

- `glacc-auto schedule status` 确认已注册
- `systemctl --user status glacc-auto-claim.timer` 查看是否 enabled/active
- 无图形会话的机器请执行 `loginctl enable-linger $USER`
- 确认已 `glacc-auto login`，且 `~/.config/glacc-auto/credentials.json` 存在
- 日志：`~/.config/glacc-auto/logs/`

**CLI 退出码**

- `0` 成功（含「已有实例在领、本次跳过」）
- `1` 领取未完成 / 命令错误
- `10` 需要重新短信登录
- `2` 未处理异常

## 数据与隐私

- 手机号、账号 ID、登录令牌**仅保存在本机**数据目录，不会上传到任何第三方服务器
- 程序不收集、不上报任何使用数据与统计信息

## 从源码构建

### Windows GUI

需要 .NET 10 SDK、Windows 11 SDK 与 MSVC 生成工具（Visual Studio Build Tools 的 C++ 生成工具）。仓库根目录的 `package.ps1` 提供了一键打包：

```powershell
.\package.ps1                # 发布 + 暂存 + 打包 zip（版本号读自 GlaccAuto.Gui.csproj）
.\package.ps1 -Launch        # 打包后直接启动
.\package.ps1 -Version 0.2.0 # 覆盖版本号
```

若工具链装在非默认位置，可用 `-BuildToolsRoot` 指定，或设置环境变量 `GLACC_BUILDTOOLS`。

### Linux CLI

```bash
dotnet publish src/GlaccAuto.Cli/GlaccAuto.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o dist/linux/linux-x64
```

aarch64 把 `-r linux-x64` 换成 `-r linux-arm64`。或直接用 `scripts/install-linux.sh`。

## 许可

本项目以 **[PolyForm Noncommercial License 1.0.0](LICENSE)** 授权：

- ✅ 允许个人学习、研究、实验、业余爱好等**非商业用途**
- ✅ 允许修改、二次开发与再分发
- ❌ **禁止任何商业或盈利性使用**
- 📌 分发时必须保留版权声明（见 [NOTICE](NOTICE)）与许可条款文本

> 注意：这是**非商业许可**，不等于 OSI 认可的开源许可。源码公开、可自由用于非商业目的，但**不可商用**。

使用前请务必阅读 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

Copyright (c) 2026 JiangXu26710
