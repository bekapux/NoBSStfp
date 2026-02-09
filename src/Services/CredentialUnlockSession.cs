using System;
using System.Collections.Generic;
using NoBSSftp.Models;

namespace NoBSSftp.Services;

public static class CredentialUnlockSession
{
    private static readonly object Gate = new();
    private static readonly TimeSpan SessionDuration = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, CredentialSecrets> CachedSecrets = new(StringComparer.Ordinal);
    private static DateTime _expiresAtUtc = DateTime.MinValue;

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                var nowUtc = DateTime.UtcNow;
                if (!IsActiveCore(nowUtc))
                {
                    InvalidateCore();
                    return false;
                }

                return true;
            }
        }
    }

    public static void Activate()
    {
        lock (Gate)
            TouchCore(DateTime.UtcNow);
    }

    public static void Invalidate()
    {
        lock (Gate)
            InvalidateCore();
    }

    public static bool TryGetSecrets(string profileId,
        out CredentialSecrets? secrets)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0)
        {
            secrets = null;
            return false;
        }

        lock (Gate)
        {
            var nowUtc = DateTime.UtcNow;
            if (!IsActiveCore(nowUtc))
            {
                InvalidateCore();
                secrets = null;
                return false;
            }

            if (!CachedSecrets.TryGetValue(id, out var cached))
            {
                secrets = null;
                return false;
            }

            TouchCore(nowUtc);
            secrets = CloneSecrets(cached);
            return true;
        }
    }

    public static void CacheSecrets(string profileId,
        CredentialSecrets secrets)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0 || secrets is null)
            return;

        lock (Gate)
        {
            var nowUtc = DateTime.UtcNow;
            if (!IsActiveCore(nowUtc))
            {
                InvalidateCore();
                return;
            }

            CachedSecrets[id] = CloneSecrets(secrets);
            TouchCore(nowUtc);
        }
    }

    public static void RemoveSecrets(string profileId)
    {
        var id = NormalizeProfileId(profileId);
        if (id.Length == 0)
            return;

        lock (Gate)
        {
            CachedSecrets.Remove(id);
            if (!IsActiveCore(DateTime.UtcNow))
                InvalidateCore();
        }
    }

    private static bool IsActiveCore(DateTime nowUtc)
    {
        return _expiresAtUtc > nowUtc;
    }

    private static void TouchCore(DateTime nowUtc)
    {
        _expiresAtUtc = nowUtc + SessionDuration;
    }

    private static void InvalidateCore()
    {
        _expiresAtUtc = DateTime.MinValue;
        CachedSecrets.Clear();
    }

    private static string NormalizeProfileId(string profileId)
    {
        return profileId?.Trim() ?? string.Empty;
    }

    private static CredentialSecrets CloneSecrets(CredentialSecrets source)
    {
        return new CredentialSecrets
        {
            Password = source.Password ?? string.Empty,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase ?? string.Empty
        };
    }
}
