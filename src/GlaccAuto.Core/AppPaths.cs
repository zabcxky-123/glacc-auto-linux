namespace GlaccAuto.Core;

/// <summary>
/// 本机数据目录。Windows：%APPDATA%\glacc-auto；Linux：$XDG_CONFIG_HOME/glacc-auto 或 ~/.config/glacc-auto。
/// 可用环境变量 GLACC_HOME 覆盖（便于 systemd / 容器指定独立目录）。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string CredentialsFile => Path.Combine(Root, "credentials.json");
    public static string LogsDir => Path.Combine(Root, "logs");

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("GLACC_HOME");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env.Trim());
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "glacc-auto");
    }
}
