using System.Runtime.InteropServices;
using System.Windows.Controls;

namespace NordVault.App;
internal static class UiSecrets
{
    public static char[] Read(PasswordBox box)
    {
        using var secret=box.SecurePassword;var bstr=Marshal.SecureStringToBSTR(secret);
        try { var result=new char[secret.Length];Marshal.Copy(bstr,result,0,result.Length);return result; }
        finally {Marshal.ZeroFreeBSTR(bstr);}
    }
}
