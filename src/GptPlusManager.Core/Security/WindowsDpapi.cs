using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GptPlusManager.Core.Security;

internal static class WindowsDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data) => Transform(data, true);

    public static byte[] Unprotect(byte[] data) => Transform(data, false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Legacy token encryption uses Windows DPAPI CurrentUser.");
        }

        var input = new DataBlob { Size = data.Length };
        var output = new DataBlob();
        input.Data = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        try
        {
            if (data.Length > 0)
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
            }

            var succeeded = protect
                ? CryptProtectData(
                    ref input,
                    "GptPlusManager",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output)
                : CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref output);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    protect ? "DPAPI encryption failed." : "DPAPI decryption failed.");
            }

            var result = new byte[output.Size];
            if (output.Size > 0)
            {
                Marshal.Copy(output.Data, result, 0, output.Size);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                _ = LocalFree(output.Data);
            }
        }
    }
}
