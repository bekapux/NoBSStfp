using System.Collections.Generic;

namespace NoBSSftp.Models;

public enum AuthMethodPreference
{
    Agent,
    PrivateKey,
    Password
}

public static class AuthPreferenceOrder
{
    private static readonly AuthMethodPreference[] LegacyPasswordFirst =
    [
        AuthMethodPreference.Password,
        AuthMethodPreference.Agent,
        AuthMethodPreference.PrivateKey
    ];

    private static readonly AuthMethodPreference[] LegacyPrivateKeyFirst =
    [
        AuthMethodPreference.PrivateKey,
        AuthMethodPreference.Agent,
        AuthMethodPreference.Password
    ];

    public static List<AuthMethodPreference> Normalize(IEnumerable<AuthMethodPreference>? configuredOrder,
        bool legacyUsePrivateKey)
    {
        var normalized = new List<AuthMethodPreference>(3);

        if (configuredOrder is not null)
        {
            foreach (var method in configuredOrder)
                AppendIfMissing(normalized, method);
        }

        var fallback = legacyUsePrivateKey ? LegacyPrivateKeyFirst : LegacyPasswordFirst;
        foreach (var method in fallback)
            AppendIfMissing(normalized, method);

        return normalized;
    }

    public static string ToLabel(AuthMethodPreference method)
    {
        return method switch
        {
            AuthMethodPreference.Agent => "SSH agent",
            AuthMethodPreference.PrivateKey => "Private key file",
            AuthMethodPreference.Password => "Password",
            _ => method.ToString()
        };
    }

    private static void AppendIfMissing(List<AuthMethodPreference> ordered,
        AuthMethodPreference method)
    {
        if (!ordered.Contains(method))
            ordered.Add(method);
    }
}
