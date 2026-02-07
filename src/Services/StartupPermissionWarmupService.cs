using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NoBSSftp.Services;

public static class StartupPermissionWarmupService
{
    private static readonly byte[] MdnsQueryPacket =
    [
        0x00, 0x00, // ID
        0x00, 0x00, // Flags
        0x00, 0x01, // Questions
        0x00, 0x00, // Answer RRs
        0x00, 0x00, // Authority RRs
        0x00, 0x00, // Additional RRs
        0x09, (byte)'_', (byte)'s', (byte)'e', (byte)'r', (byte)'v', (byte)'i', (byte)'c', (byte)'e', (byte)'s',
        0x07, (byte)'_', (byte)'d', (byte)'n', (byte)'s', (byte)'-', (byte)'s', (byte)'d',
        0x04, (byte)'_', (byte)'u', (byte)'d', (byte)'p',
        0x05, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l',
        0x00,
        0x00, 0x0c, // Type PTR
        0x00, 0x01 // Class IN
    ];

    public static async Task WarmUpAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        WarmUpLocalNetworkAccess();
        await WarmUpKeychainAccessAsync();
    }

    private static void WarmUpLocalNetworkAccess()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SendTimeout = 1000;
            socket.ReceiveTimeout = 1000;
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            _ = socket.SendTo(MdnsQueryPacket, endpoint);
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Local network permission warm-up failed: {ex.Message}");
        }
    }

    private static async Task WarmUpKeychainAccessAsync()
    {
        try
        {
            var store = new SecureCredentialStore();
            await store.WarmUpAccessAsync();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Keychain warm-up failed: {ex.Message}");
        }
    }
}
