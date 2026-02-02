using System;
using System.Text;

namespace NoBSSftp.Services;

public interface IEncryptionService
{
    string Protect(string clearText);
    string Unprotect(string encryptedText);
}

public class EncryptionService : IEncryptionService
{
    // In future versions of the app I will use:
    // macOS: Keychain (via Xamarin.Essentials or generic interop)
    // Windows: ProtectedData.Protect (DPAPI)
    // Linux: LibSecret/Gnome Keyring
    
    // For this prototype, we will return the text as-is or base64 to simulate handling
    // WARNING: Not saving passwords for now.
    
    public string Protect(string clearText)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(clearText));
    }

    public string Unprotect(string encryptedText)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encryptedText));
        }
        catch
        {
            return string.Empty;
        }
    }
}
