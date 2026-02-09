using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public static class ExternalTerminalService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object LaunchGate = new();
    private static DateTime _lastLaunchUtc = DateTime.MinValue;

    public static void OpenSshSession(ServerProfile profile)
    {
        if (ShouldSuppressDuplicateLaunch())
            return;

        if (OperatingSystem.IsMacOS())
        {
            LaunchMacTerminal(profile);
            return;
        }

        var sshCommand = BuildSshShellCommand(profile);

        if (OperatingSystem.IsWindows())
        {
            StartProcess("powershell", "-NoExit", "-Command", sshCommand);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LaunchLinuxTerminal(sshCommand);
            return;
        }

        throw new PlatformNotSupportedException("External terminal launch is not supported on this platform.");
    }

    [SupportedOSPlatform("macos")]
    private static void LaunchMacTerminal(ServerProfile profile)
    {
        var command = BuildMacCommand(profile);
        var escapedCommand = EscapeAppleScript(command);
        var script = string.Join(
            Environment.NewLine,
            "tell application \"Terminal\"",
            "    activate",
            "    if (count of windows) = 0 then",
            "        do script \"\"",
            "        delay 0.05",
            "    end if",
            $"    do script \"{escapedCommand}\" in selected tab of front window",
            "end tell");
        StartProcess("osascript", "-e", script);
    }

    [SupportedOSPlatform("macos")]
    private static string BuildMacCommand(ServerProfile profile)
    {
        var needsAutomation =
            !string.IsNullOrEmpty(profile.Password) ||
            !string.IsNullOrEmpty(profile.PrivateKeyPassphrase);

        if (!needsAutomation)
            return BuildSshShellCommand(profile);

        if (!File.Exists("/usr/bin/expect"))
        {
            throw new InvalidOperationException(
                "Automatic terminal login requires '/usr/bin/expect', but it was not found.");
        }

        var scriptPath = WriteExpectScript(profile);
        return $"/usr/bin/expect {QuoteForPosixShell(scriptPath)}; rm -f {QuoteForPosixShell(scriptPath)}";
    }

    [SupportedOSPlatform("macos")]
    private static string WriteExpectScript(ServerProfile profile)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nobs-sftp-ssh-{Guid.NewGuid():N}.exp");
        var script = BuildExpectScript(profile);
        File.WriteAllText(path, script, Utf8NoBom);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static string BuildExpectScript(ServerProfile profile)
    {
        var sshArgs = BuildSshExpectArguments(profile);
        var hasPassword = !string.IsNullOrEmpty(profile.Password);
        var hasKeyPassphrase = !string.IsNullOrEmpty(profile.PrivateKeyPassphrase);
        var lines = new List<string>
        {
            "proc handoff {} {",
            "    log_user 1",
            "    interact",
            "    exit 0",
            "}",
            "proc handoff_with_clear {} {",
            "    log_user 1",
            "    send -- \"\\r\"",
            "    send -- \"clear\\r\"",
            "    interact",
            "    exit 0",
            "}",
            "set timeout 10",
            "log_user 0",
            $"spawn {string.Join(' ', sshArgs)}",
            "expect {",
            "    -re {(?i).*are you sure you want to continue connecting.*} { send -- \"yes\\r\"; exp_continue }"
        };

        if (hasPassword)
        {
            lines.Add(
                $"    -re {{(?i).*password.*:}} {{ send -- \"{EscapeForTclDoubleQuote(profile.Password)}\\r\"; set timeout 6; exp_continue }}");
        }

        if (hasKeyPassphrase)
        {
            lines.Add(
                $"    -re {{(?i).*(enter )?pass( ?|-)phrase.*:}} {{ send -- \"{EscapeForTclDoubleQuote(profile.PrivateKeyPassphrase)}\\r\"; set timeout 6; exp_continue }}");
        }

        // Unknown auth prompt: give terminal control to user immediately.
        lines.Add("    -re {(?i).*(password|passphrase).*:} { handoff }");
        lines.Add("    -re {(?i).*(permission denied|authentication failed).*} { log_user 1; send_user \"\\nAuthentication failed.\\n\"; exit 1 }");
        lines.Add("    -re {(?m).*[#$>%] ?$} { handoff_with_clear }");
        // If no prompt match, still hand control over to keep UI responsive.
        lines.Add("    timeout { handoff }");
        lines.Add("    eof { log_user 1; exit 0 }");
        lines.Add("}");

        return string.Join('\n', lines);
    }

    private static string BuildSshShellCommand(ServerProfile profile)
    {
        var args = BuildSshArgumentsRaw(profile);
        return JoinShellCommand(args);
    }

    private static List<string> BuildSshExpectArguments(ServerProfile profile)
    {
        var args = BuildSshArgumentsRaw(profile);
        for (var i = 0; i < args.Count; i++)
            args[i] = QuoteForTclBrace(args[i]);

        return args;
    }

    private static List<string> BuildSshArgumentsRaw(ServerProfile profile)
    {
        var target = BuildTarget(profile);
        var args = new List<string> { "ssh" };
        AppendTargetConnectionOptions(args, profile);
        args.Add(target);
        return args;
    }

    private static void AppendTargetConnectionOptions(List<string> args, ServerProfile profile)
    {
        if (profile.Port > 0 && profile.Port != 22)
        {
            args.Add("-p");
            args.Add(profile.Port.ToString(CultureInfo.InvariantCulture));
        }

        var authOrder = AuthPreferenceOrder.Normalize(profile.AuthPreferenceOrder, profile.UsePrivateKey);
        var supportsAgent = authOrder.Contains(AuthMethodPreference.Agent);
        var supportsPrivateKey = authOrder.Contains(AuthMethodPreference.PrivateKey);
        var supportsPassword = authOrder.Contains(AuthMethodPreference.Password);

        if (supportsPrivateKey && !string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            var normalizedKeyPath = NormalizeAndValidatePrivateKeyPath(profile.PrivateKeyPath);
            args.Add("-i");
            args.Add(normalizedKeyPath);

            if (!supportsAgent)
            {
                // Without agent fallback, force the explicit identity file only.
                args.Add("-o");
                args.Add("IdentitiesOnly=yes");
            }
        }

        args.Add("-o");
        args.Add($"PreferredAuthentications={BuildPreferredAuthentications(authOrder)}");

        args.Add("-o");
        args.Add(supportsAgent || supportsPrivateKey ? "PubkeyAuthentication=yes" : "PubkeyAuthentication=no");

        if (supportsAgent || supportsPrivateKey)
        {
            args.Add("-o");
            args.Add("PubkeyAcceptedAlgorithms=+ssh-rsa");
        }

        if (!supportsPassword)
        {
            args.Add("-o");
            args.Add("PasswordAuthentication=no");
            args.Add("-o");
            args.Add("KbdInteractiveAuthentication=no");
            return;
        }

        args.Add("-o");
        args.Add("KbdInteractiveAuthentication=yes");
    }

    private static string BuildPreferredAuthentications(IReadOnlyList<AuthMethodPreference> authOrder)
    {
        var preferred = new List<string>(3);
        var hasPublicKey = false;

        foreach (var authMethod in authOrder)
        {
            switch (authMethod)
            {
                case AuthMethodPreference.Agent:
                case AuthMethodPreference.PrivateKey:
                    if (!hasPublicKey)
                    {
                        preferred.Add("publickey");
                        hasPublicKey = true;
                    }

                    break;
                case AuthMethodPreference.Password:
                    preferred.Add("password");
                    preferred.Add("keyboard-interactive");
                    break;
            }
        }

        if (preferred.Count == 0)
            return "publickey,password,keyboard-interactive";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<string>(preferred.Count);
        foreach (var method in preferred)
        {
            if (seen.Add(method))
                deduplicated.Add(method);
        }

        return string.Join(',', deduplicated);
    }

    private static bool ShouldSuppressDuplicateLaunch()
    {
        lock (LaunchGate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastLaunchUtc < TimeSpan.FromSeconds(2))
                return true;

            _lastLaunchUtc = now;
            return false;
        }
    }

    private static string NormalizeAndValidatePrivateKeyPath(string configuredPath)
    {
        var normalized = NormalizePrivateKeyPath(configuredPath);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Private key path is required for key authentication.");

        if (!File.Exists(normalized))
            throw new InvalidOperationException($"Private key file was not found: {normalized}");

        return normalized;
    }

    private static string NormalizePrivateKeyPath(string rawPath)
    {
        var path = rawPath.Trim();

        if (path.Length >= 2 &&
            ((path.StartsWith('"') && path.EndsWith('"')) ||
             (path.StartsWith('\'') && path.EndsWith('\''))))
        {
            path = path[1..^1];
        }

        path = Environment.ExpandEnvironmentVariables(path);

        if (path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                path = home;
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) ||
                 path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var relative = path[2..]
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                path = Path.Combine(home, relative);
            }
        }

        if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path))
            path = Path.GetFullPath(path);

        return path;
    }

    private static string BuildTarget(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("Host is required to open a terminal session.");

        if (string.IsNullOrWhiteSpace(profile.Username))
            return profile.Host;

        return $"{profile.Username}@{profile.Host}";
    }

    private static void LaunchLinuxTerminal(string sshCommand)
    {
        var candidates = new List<(string FileName, string[] Args)>
        {
            ("x-terminal-emulator", ["-e", "bash", "-lc", sshCommand]),
            ("gnome-terminal", ["--", "bash", "-lc", sshCommand]),
            ("konsole", ["-e", "bash", "-lc", sshCommand]),
            ("xterm", ["-e", "bash", "-lc", sshCommand])
        };

        foreach (var candidate in candidates)
        {
            try
            {
                StartProcess(candidate.FileName, candidate.Args);
                return;
            }
            catch
            {
                // Try the next terminal candidate.
            }
        }

        throw new InvalidOperationException("No supported terminal emulator was found.");
    }

    private static void StartProcess(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        var process = Process.Start(startInfo);
        if (process is null)
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
    }

    private static string EscapeForTclDoubleQuote(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    private static string QuoteForTclBrace(string value)
    {
        return "{" +
               value
                   .Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("{", "\\{", StringComparison.Ordinal)
                   .Replace("}", "\\}", StringComparison.Ordinal) +
               "}";
    }

    private static string QuoteForPosixShell(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    private static string JoinShellCommand(IEnumerable<string> args)
    {
        var command = new StringBuilder();
        foreach (var arg in args)
        {
            if (command.Length > 0)
                command.Append(' ');

            command.Append(QuoteForPosixShell(arg));
        }

        return command.ToString();
    }

    private static string EscapeAppleScript(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
