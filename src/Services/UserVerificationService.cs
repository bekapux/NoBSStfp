using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public interface IUserVerificationService
{
    Task<bool> VerifyForConnectionAsync(ServerProfile profile);
}

public sealed class UserVerificationService : IUserVerificationService
{
    public async Task<bool> VerifyForConnectionAsync(ServerProfile profile)
    {
        if (!OperatingSystem.IsMacOS())
            return true;

        if (CredentialUnlockSession.IsActive)
            return true;

        try
        {
            var reason = BuildReason(profile);
            var (exitCode, stdOut, stdErr) = await RunMacVerificationAsync(reason);
            var verdict = stdOut.Trim();

            if (exitCode == 0 && verdict.Equals("OK", StringComparison.Ordinal))
            {
                CredentialUnlockSession.Activate();
                return true;
            }

            if (verdict.Equals("UNAVAILABLE", StringComparison.Ordinal))
                LoggingService.Warn($"User verification unavailable: {stdErr.Trim()}");
            else if (!string.IsNullOrWhiteSpace(stdErr))
                LoggingService.Warn($"User verification failed: {stdErr.Trim()}");

            return false;
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"User verification failed: {ex.Message}");
            return false;
        }
    }

    private static string BuildReason(ServerProfile profile)
    {
        var host = string.IsNullOrWhiteSpace(profile.Host) ? "server" : profile.Host.Trim();
        return $"NoBSSftp: verify to connect to {host}";
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunMacVerificationAsync(string reason)
    {
        const string script = """
ObjC.import("LocalAuthentication");
ObjC.import("Foundation");

function writeErr(message) {
  var text = $(String(message) + "\n");
  $.NSFileHandle.fileHandleWithStandardError.writeData(text.dataUsingEncoding($.NSUTF8StringEncoding));
}

function run(argv) {
  var reason = argv.length > 0 ? ObjC.unwrap(argv[0]) : "Verify to continue";
  var context = $.LAContext.alloc.init;
  var canEvalErr = Ref();
  var policy = $.LAPolicyDeviceOwnerAuthentication;

  if (!context.canEvaluatePolicyError(policy, canEvalErr)) {
    if (canEvalErr[0]) {
      writeErr(ObjC.unwrap(canEvalErr[0].localizedDescription));
    }
    return "UNAVAILABLE";
  }

  var finished = false;
  var success = false;

  context.evaluatePolicyLocalizedReasonReply(
    policy,
    $(reason),
    function(ok, evalErr) {
      success = !!ok;
      if (!ok && evalErr) {
        writeErr(ObjC.unwrap(evalErr.localizedDescription));
      }
      finished = true;
    }
  );

  while (!finished) {
    $.NSRunLoop.currentRunLoop.runUntilDate($.NSDate.dateWithTimeIntervalSinceNow(0.05));
  }
  return success ? "OK" : "FAIL";
}
""";

        var startInfo =
            new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add("JavaScript");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(reason);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        return (process.ExitCode, stdOut, stdErr);
    }
}
