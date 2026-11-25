using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FastTorrentDownload.Services;

/// <summary>
/// Encrypts small secrets with Windows DPAPI for the current user, so settings.json never holds
/// them in plain text. A protected value only decrypts for the same Windows account on the same PC.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private const int UiForbidden = 0x1;

    public static string Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return Prefix + Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), protect: true));
    }

    /// <summary>Returns the secret, or null when the value is empty, malformed or belongs to another account.</summary>
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value[Prefix.Length..]), protect: false));
        }
        catch (Exception exception) when (exception is FormatException or Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var source = new Blob { Size = input.Length, Data = pinned.AddrOfPinnedObject() };
            var succeeded = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var output = new byte[result.Size];
                Marshal.Copy(result.Data, output, 0, result.Size);
                return output;
            }
            finally
            {
                LocalFree(result.Data);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
