using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace GlaccAuto.Core.Scheduling;

/// <summary>
/// 每日定时领取任务管理：Windows 用计划任务，Linux 用 systemd --user timer（不可用时回退 crontab）。
/// 任务到点以 --scheduled 参数拉起本应用。
/// </summary>
public static class ScheduledTaskManager
{
    public const string TaskName = "glacc-auto-claim";
    public const string LaunchArgument = "--scheduled";
    public const string LinuxUnit = "glacc-auto-claim";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>注册/注销失败时抛出，消息可直接展示给用户。</summary>
    public sealed class ScheduledTaskException(string message) : Exception(message);

    /// <summary>任务定义快照。</summary>
    public sealed record TaskSnapshot(
        bool Enabled,
        bool TriggerEnabled,
        bool Daily,
        TimeSpan StartTime,
        string Command,
        string Arguments,
        bool StartWhenAvailable,
        bool DisallowStartIfOnBatteries,
        bool StopIfGoingOnBatteries);

    /// <summary>
    /// 快照是否与预期一致：每日 time 时刻、当前 exe + --scheduled、任务与触发器均启用。
    /// </summary>
    public static bool MatchesExpectation(TaskSnapshot? snapshot, TimeSpan time)
    {
        if (snapshot is null) return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        return snapshot.Enabled
            && snapshot.TriggerEnabled
            && snapshot.Daily
            && snapshot.StartTime.Hours == time.Hours
            && snapshot.StartTime.Minutes == time.Minutes
            && string.Equals(snapshot.Command.Trim('"'), exe, StringComparison.OrdinalIgnoreCase)
            && snapshot.Arguments.Trim() == LaunchArgument
            && !snapshot.StartWhenAvailable
            && !snapshot.DisallowStartIfOnBatteries
            && !snapshot.StopIfGoingOnBatteries;
    }

    public static bool TryQuery(out TaskSnapshot? snapshot)
    {
        if (OperatingSystem.IsLinux()) return Linux.TryQuery(out snapshot);
        if (OperatingSystem.IsWindows()) return Windows.TryQuery(out snapshot);
        snapshot = null;
        return true;
    }

    public static void Register(TimeSpan time)
    {
        if (OperatingSystem.IsLinux()) { Linux.Register(time); return; }
        if (OperatingSystem.IsWindows()) { Windows.Register(time); return; }
        throw new ScheduledTaskException("当前系统不支持自动注册定时任务，请改用 cron 手动配置");
    }

    public static void Unregister()
    {
        if (OperatingSystem.IsLinux()) { Linux.Unregister(); return; }
        if (OperatingSystem.IsWindows()) { Windows.Unregister(); return; }
    }

    // ── Windows（schtasks）──────────────────────────────────────────────

    private static class Windows
    {
        public static bool TryQuery(out TaskSnapshot? snapshot)
        {
            snapshot = null;
            string stdout;
            try
            {
                var (code, output, _) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
                if (code != 0) return true;
                stdout = output;
            }
            catch
            {
                return false;
            }
            try
            {
                var root = XDocument.Parse(stdout).Root;
                var settings = root?.Element(Ns + "Settings");
                var trigger = root?.Element(Ns + "Triggers")?.Element(Ns + "CalendarTrigger");
                var exec = root?.Element(Ns + "Actions")?.Element(Ns + "Exec");
                var start = trigger?.Element(Ns + "StartBoundary")?.Value;
                if (settings is null || trigger is null || exec is null || start is null) return false;
                if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var st))
                    return false;
                var daily = int.TryParse(
                    trigger.Element(Ns + "ScheduleByDay")?.Element(Ns + "DaysInterval")?.Value,
                    out var days) && days == 1;
                snapshot = new TaskSnapshot(
                    Enabled: ElementText(settings, "Enabled") != "false",
                    TriggerEnabled: ElementText(trigger, "Enabled") != "false",
                    Daily: daily,
                    StartTime: st.TimeOfDay,
                    Command: ElementText(exec, "Command"),
                    Arguments: ElementText(exec, "Arguments"),
                    StartWhenAvailable: ElementText(settings, "StartWhenAvailable") == "true",
                    DisallowStartIfOnBatteries: ElementText(settings, "DisallowStartIfOnBatteries") != "false",
                    StopIfGoingOnBatteries: ElementText(settings, "StopIfGoingOnBatteries") != "false");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Register(TimeSpan time)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new ScheduledTaskException("无法确定应用可执行文件路径");
            var tmp = Path.Combine(Path.GetTempPath(), "glacc-auto-task.xml");
            File.WriteAllText(tmp, BuildTaskXml(exe, time), Encoding.Unicode);
            try
            {
                var (code, _, stderr) = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"");
                if (code != 0)
                    throw new ScheduledTaskException(Describe(stderr));
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* 临时文件清理失败不影响注册结果 */ }
            }
        }

        public static void Unregister()
        {
            var (code, _, stderr) = RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
            if (code == 0) return;
            if (TryQuery(out var snapshot) && snapshot is null) return;
            throw new ScheduledTaskException(Describe(stderr));
        }

        private static string BuildTaskXml(string exe, TimeSpan time)
        {
            var start = $"{DateTime.Today:yyyy-MM-dd}T{time.Hours:00}:{time.Minutes:00}:00";
            return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="{Ns}">
                  <RegistrationInfo>
                    <Description>glacc-auto 每日定时领取</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <CalendarTrigger>
                      <StartBoundary>{start}</StartBoundary>
                      <Enabled>true</Enabled>
                      <ScheduleByDay>
                        <DaysInterval>1</DaysInterval>
                      </ScheduleByDay>
                    </CalendarTrigger>
                  </Triggers>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>false</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <RunOnlyIfIdle>false</RunOnlyIfIdle>
                    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
                    <Priority>7</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{SecurityElement.Escape(exe)}</Command>
                      <Arguments>{LaunchArgument}</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
        }

        private static string ElementText(XElement parent, string name) =>
            parent.Element(Ns + name)?.Value.Trim() ?? "";

        private const int SchtasksTimeoutMs = 15_000;

        private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string args)
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            p.Start();
            using var outMs = new MemoryStream();
            using var errMs = new MemoryStream();
            var outTask = p.StandardOutput.BaseStream.CopyToAsync(outMs);
            var errTask = p.StandardError.BaseStream.CopyToAsync(errMs);
            if (!p.WaitForExit(SchtasksTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 进程可能已自行退出 */ }
                throw new ScheduledTaskException("schtasks 调用超时");
            }
            outTask.Wait();
            errTask.Wait();
            return (p.ExitCode, Decode(outMs.ToArray()), Decode(errMs.ToArray()));
        }

        private static string Decode(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes); }
                catch { return Encoding.UTF8.GetString(bytes); }
            }
        }

        private static string Describe(string stderr) =>
            string.IsNullOrWhiteSpace(stderr) ? "schtasks 调用失败" : stderr.Trim();
    }

    // ── Linux（systemd --user，失败则 crontab）──────────────────────────

    private static class Linux
    {
        private static string UserUnitDir
        {
            get
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                var config = string.IsNullOrWhiteSpace(xdg)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                    : xdg;
                return Path.Combine(config, "systemd", "user");
            }
        }

        private static string ServicePath => Path.Combine(UserUnitDir, $"{LinuxUnit}.service");
        private static string TimerPath => Path.Combine(UserUnitDir, $"{LinuxUnit}.timer");

        public static bool TryQuery(out TaskSnapshot? snapshot)
        {
            snapshot = null;
            try
            {
                if (HasSystemdUser())
                {
                    var (code, stdout, _) = Run("systemctl", $"--user show {LinuxUnit}.timer --property=LoadState,UnitFileState,OnCalendar", 8_000);
                    if (code != 0 || Prop(stdout, "LoadState") is "not-found" or "masked")
                    {
                        if (File.Exists(TimerPath)) return false;
                        return true;
                    }
                    var state = Prop(stdout, "UnitFileState");
                    var calendar = Prop(stdout, "OnCalendar");
                    var enabled = state is "enabled" or "enabled-runtime" or "static";
                    var time = ParseOnCalendar(calendar);
                    var (cmd, args) = ReadServiceExec();
                    snapshot = new TaskSnapshot(
                        Enabled: enabled,
                        TriggerEnabled: enabled,
                        Daily: true,
                        StartTime: time,
                        Command: cmd,
                        Arguments: args,
                        StartWhenAvailable: false,
                        DisallowStartIfOnBatteries: false,
                        StopIfGoingOnBatteries: false);
                    return true;
                }

                return TryQueryCron(out snapshot);
            }
            catch
            {
                return false;
            }
        }

        public static void Register(TimeSpan time)
        {
            var exe = ResolveLinuxExecutable();
            if (HasSystemdUser())
            {
                try
                {
                    Directory.CreateDirectory(UserUnitDir);
                    File.WriteAllText(ServicePath, BuildService(exe));
                    File.WriteAllText(TimerPath, BuildTimer(time));
                    var (reload, _, reloadErr) = Run("systemctl", "--user daemon-reload", 15_000);
                    if (reload != 0)
                        throw new ScheduledTaskException($"systemctl daemon-reload 失败：{reloadErr}");
                    var (en, _, enErr) = Run("systemctl", $"--user enable --now {LinuxUnit}.timer", 15_000);
                    if (en != 0)
                        throw new ScheduledTaskException($"启用 systemd timer 失败：{enErr}");
                    return;
                }
                catch (ScheduledTaskException)
                {
                    // 用户总线未启动等：回退 crontab，保证「每天能领」这个目标仍可达
                }
            }
            RegisterCron(exe, time);
        }

        public static void Unregister()
        {
            if (HasSystemdUser())
            {
                Run("systemctl", $"--user disable --now {LinuxUnit}.timer", 15_000);
                try { File.Delete(ServicePath); } catch { /* 幂等 */ }
                try { File.Delete(TimerPath); } catch { /* 幂等 */ }
                Run("systemctl", "--user daemon-reload", 15_000);
                return;
            }
            UnregisterCron();
        }

        private static bool HasSystemdUser()
        {
            try
            {
                var (code, _, _) = Run("systemctl", "--user is-system-running", 5_000);
                // 0=running，1=degraded；offline/unknown 时用户总线不可用，应走 crontab
                return code is 0 or 1;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveLinuxExecutable()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new ScheduledTaskException("无法确定应用可执行文件路径");
            return exe;
        }

        private static string BuildService(string exe)
        {
            var work = Path.GetDirectoryName(exe) ?? "";
            var escaped = EscapeSystemd(exe);
            return $"""
                [Unit]
                Description=glacc-auto 每日定时领取
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=oneshot
                WorkingDirectory={EscapeSystemd(work)}
                ExecStart={escaped} {LaunchArgument}
                TimeoutStartSec=7200

                [Install]
                WantedBy=default.target
                """;
        }

        private static string BuildTimer(TimeSpan time) => $"""
            [Unit]
            Description=glacc-auto 每日定时领取

            [Timer]
            OnCalendar=*-*-* {time.Hours:00}:{time.Minutes:00}:00
            Persistent=true
            RandomizedDelaySec=90
            AccuracySec=1min
            Unit={LinuxUnit}.service

            [Install]
            WantedBy=timers.target
            """;

        private static string EscapeSystemd(string path) =>
            path.Contains(' ') ? $"\"{path.Replace("\"", "\\\"")}\"" : path;

        private static (string Command, string Arguments) ReadServiceExec()
        {
            try
            {
                if (!File.Exists(ServicePath)) return ("", "");
                foreach (var line in File.ReadAllLines(ServicePath))
                {
                    if (!line.StartsWith("ExecStart=", StringComparison.Ordinal)) continue;
                    var rest = line["ExecStart=".Length..].Trim();
                    var idx = rest.LastIndexOf(' ');
                    if (idx <= 0) return (rest.Trim('"'), "");
                    return (rest[..idx].Trim().Trim('"'), rest[(idx + 1)..].Trim());
                }
            }
            catch { /* 解析失败按空处理 */ }
            return ("", "");
        }

        private static TimeSpan ParseOnCalendar(string calendar)
        {
            // 例：*-*-* 08:00:00
            var parts = calendar.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (TimeSpan.TryParse(p, CultureInfo.InvariantCulture, out var t))
                    return new TimeSpan(t.Hours, t.Minutes, 0);
            }
            return new TimeSpan(8, 0, 0);
        }

        private static string Prop(string stdout, string name)
        {
            foreach (var line in stdout.Split('\n'))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                if (line[..i].Trim() == name) return line[(i + 1)..].Trim();
            }
            return "";
        }

        // crontab 回退：标记行便于幂等更新
        private const string CronMark = "# glacc-auto-claim";

        private static bool TryQueryCron(out TaskSnapshot? snapshot)
        {
            snapshot = null;
            var (code, stdout, _) = Run("crontab", "-l", 8_000);
            if (code != 0) return true; // 无 crontab
            foreach (var line in stdout.Split('\n'))
            {
                if (!line.Contains(CronMark, StringComparison.Ordinal)) continue;
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#')) continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6) continue;
                if (!int.TryParse(parts[0], out var minute) || !int.TryParse(parts[1], out var hour))
                    continue;
                var rest = string.Join(' ', parts.Skip(5)).Replace(CronMark, "").Trim();
                var argIdx = rest.LastIndexOf(LaunchArgument, StringComparison.Ordinal);
                var cmd = argIdx > 0 ? rest[..argIdx].Trim().Trim('"') : rest;
                snapshot = new TaskSnapshot(true, true, true, new TimeSpan(hour, minute, 0),
                    cmd, LaunchArgument, false, false, false);
                return true;
            }
            return true;
        }

        private static void RegisterCron(string exe, TimeSpan time)
        {
            var (code, existing, _) = Run("crontab", "-l", 8_000);
            var lines = code == 0
                ? existing.Split('\n').Where(l => !l.Contains(CronMark, StringComparison.Ordinal)).ToList()
                : [];
            while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            lines.Add($"{time.Minutes} {time.Hours} * * * \"{exe}\" {LaunchArgument} {CronMark}");
            ApplyCrontab(string.Join('\n', lines) + "\n");
        }

        private static void UnregisterCron()
        {
            var (code, existing, _) = Run("crontab", "-l", 8_000);
            if (code != 0) return;
            var lines = existing.Split('\n').Where(l => !l.Contains(CronMark, StringComparison.Ordinal)).ToList();
            ApplyCrontab(string.Join('\n', lines) + "\n");
        }

        private static void ApplyCrontab(string text)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"glacc-cron-{Guid.NewGuid():N}");
            File.WriteAllText(tmp, text);
            try
            {
                var (code, _, stderr) = Run("crontab", tmp, 8_000);
                if (code != 0)
                    throw new ScheduledTaskException(string.IsNullOrWhiteSpace(stderr) ? "crontab 写入失败" : stderr.Trim());
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* 忽略 */ }
            }
        }

        private static (int ExitCode, string StdOut, string StdErr) Run(string file, string args, int timeoutMs)
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                throw new ScheduledTaskException($"{file} 调用超时");
            }
            return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
    }
}
