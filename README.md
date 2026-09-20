# glacc-auto-linux

给梨加速器时长自动领取的 **Linux 移植**：无界面 CLI，支持每天定时领取（systemd 用户定时器 / crontab）。

上游 Windows GUI：[JiangXu26710/glacc-auto](https://github.com/JiangXu26710/glacc-auto)

---

> ### ⚠️ 请先阅读
> 本项目**仅供个人学习、研究与技术交流**，**严禁任何商业或盈利性使用**。
> 本软件为非官方第三方工具，与任何第三方服务提供方**无任何关联**。
> 使用可能带来的账号限制、权益损失及法律风险**由使用者自行承担**。
> 完整条款见 **[LICENSE](LICENSE)** 与 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

## 功能

- **命令行领取** —— `login` / `claim` / `status`，登录后一次跑完当日任务
- **每日定时** —— `schedule on HH:MM`：优先 systemd `--user` timer，不可用时写入 crontab
- **错过补跑** —— systemd `Persistent=true`，开机后补跑错过的触发
- **结果通知** —— 可选 Server酱，定时领取结束后推送到微信
- **本机凭证** —— 手机号、token 只存在本机配置目录，不上传

架构：x86_64（`linux-x64`）与 aarch64（`linux-arm64`）。需要 [.NET 10 SDK](https://dotnet.microsoft.com/download) 才能从源码安装。

## 安装

```bash
git clone https://github.com/zabcxky-123/glacc-auto-linux.git
cd glacc-auto-linux
chmod +x scripts/install-linux.sh
./scripts/install-linux.sh
```

默认安装到 `~/.local/share/glacc-auto/`，并在 `~/.local/bin/glacc-auto` 放软链接。把 `~/.local/bin` 加入 `PATH`。

安装时直接打开定时：

```bash
./scripts/install-linux.sh --time 08:00
```

自定义前缀：`PREFIX=/opt/glacc-auto ./scripts/install-linux.sh`

## 使用

```bash
glacc-auto login                 # 短信登录（交互输入手机号与验证码）
glacc-auto status                # 余额与今日任务进度
glacc-auto claim                 # 立刻领取
glacc-auto schedule on 08:00     # 每天 08:00 自动领取
glacc-auto schedule status
glacc-auto schedule off
glacc-auto logout
```

`glacc-auto --scheduled` 与 `claim` 相同，供 systemd / cron 调用，结束后按配置推送 Server酱。

无桌面、SSH 服务器若希望**未登录图形会话也到点执行**：

```bash
sudo loginctl enable-linger "$USER"
```

查看下次触发 / 手动跑一次：

```bash
systemctl --user list-timers glacc-auto-claim.timer
systemctl --user start glacc-auto-claim.service
systemctl --user status glacc-auto-claim.timer
```

无 systemd 用户总线时，会给当前用户 crontab 加一行（带 `# glacc-auto-claim` 标记）。

## 配置与数据目录

默认：`${XDG_CONFIG_HOME:-$HOME/.config}/glacc-auto/`  
可用环境变量 `GLACC_HOME` 覆盖。

| 文件 | 内容 |
| --- | --- |
| `credentials.json` | 登录令牌（本机） |
| `settings.json` | 间隔、重试、Server酱、定时时刻 |
| `logs/` | 按日诊断日志，保留 7 天 |

`settings.json` 常用项：

- `ScheduledTime`：`HH:mm`（默认 `08:00`）
- `ServerKey`：Server酱 SendKey（`SCT...`）
- `IntervalMinSec` / `IntervalMaxSec`：每次推送间隔随机区间（默认 30–40 秒）
- `NetworkRetryCount`：网络失败额外重试次数（默认 3）

## 退出码

| 码 | 含义 |
| --- | --- |
| `0` | 成功（含「已有实例在领、本次跳过」） |
| `1` | 领取未完成 / 命令错误 |
| `10` | 需要重新 `glacc-auto login` |
| `2` | 未处理异常 |

## 常见问题

**定时没跑？**

1. `glacc-auto schedule status` 看是否已注册  
2. `systemctl --user status glacc-auto-claim.timer` 是否 enabled/active  
3. 无图形会话：`loginctl enable-linger $USER`  
4. 是否已登录：存在 `~/.config/glacc-auto/credentials.json`  
5. 日志：`~/.config/glacc-auto/logs/`

**领取提示 TLS 指纹库不可用**

安装目录里应有 `tls-client.so`（与 `glacc-auto` 同级，或 `runtimes/tls-client/linux/...`）。重新执行 `scripts/install-linux.sh`。

## 从源码发布

```bash
# 当前机器架构
./scripts/package-linux.sh

# 或指定 RID
./scripts/package-linux.sh linux-x64
./scripts/package-linux.sh linux-arm64
```

产物：`dist/glacc-auto-v<版本>-linux-*.tar.gz`。手动 `dotnet publish`：

```bash
dotnet publish src/GlaccAuto.Cli/GlaccAuto.Cli.csproj \
  -c Release -r linux-x64 --self-contained true \
  -o dist/linux/linux-x64
```

本仓库仍包含上游 Windows GUI 工程；Windows 打包继续用根目录 `package.ps1`。日常 Linux 使用只需 CLI。

## 数据与隐私

- 手机号、账号 ID、登录令牌**仅保存在本机**数据目录，不会上传到任何第三方服务器
- 程序不收集、不上报任何使用数据与统计信息

## 许可

本项目以 **[PolyForm Noncommercial License 1.0.0](LICENSE)** 授权（与上游相同）：

- ✅ 允许个人学习、研究、实验、业余爱好等**非商业用途**
- ✅ 允许修改、二次开发与再分发
- ❌ **禁止任何商业或盈利性使用**
- 📌 分发时必须保留版权声明（见 [NOTICE](NOTICE)）与许可条款文本

> 这是**非商业许可**，不等于 OSI 认可的开源许可。

使用前请阅读 **[DISCLAIMER.md](DISCLAIMER.md)**。

---

Copyright (c) 2026 JiangXu26710  
Linux 移植：https://github.com/zabcxky-123/glacc-auto-linux
