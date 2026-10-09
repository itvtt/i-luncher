using System.Security.Cryptography;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ILauncher.Bootstrapper;

namespace ILauncher.Services;

/// <summary>Reads the optional native-bootstrapper PAT without exposing it in the distribution.</summary>
internal sealed class BootstrapperCredentialProvider : IBootstrapperCredentialProvider
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("i-luncher bootstrapper local credential v1");
    private readonly Uri _apiOrigin;
    private readonly string _token;

    private BootstrapperCredentialProvider(Uri apiOrigin, string token)
    {
        _apiOrigin = apiOrigin;
        _token = token;
    }

    internal static BootstrapperCredentialProvider Load(string apiUrl)
    {
        var api = new Uri(apiUrl.TrimEnd('/') + "/", UriKind.Absolute);
        string token = string.Empty;
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "i-luncher", "bootstrapper-settings.bin");
            byte[] protectedBytes = File.ReadAllBytes(path);
            byte[] plainBytes = UnprotectCurrentUser(protectedBytes, Entropy);
            using var document = JsonDocument.Parse(plainBytes);
            if (document.RootElement.TryGetProperty("token", out var value))
                token = value.GetString() ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or CryptographicException or JsonException or ArgumentException)
        {
            // No stored credential is the normal public-release case.
        }
        return new BootstrapperCredentialProvider(api, token);
    }

    public string GetToken(Uri apiOrigin) => SameOrigin(_apiOrigin, apiOrigin) ? _token : string.Empty;

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static byte[] UnprotectCurrentUser(byte[] protectedBytes, byte[] entropy)
    {
        IntPtr inputBuffer = IntPtr.Zero;
        IntPtr entropyBuffer = IntPtr.Zero;
        IntPtr outputBuffer = IntPtr.Zero;
        try
        {
            inputBuffer = Marshal.AllocHGlobal(protectedBytes.Length);
            entropyBuffer = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(protectedBytes, 0, inputBuffer, protectedBytes.Length);
            Marshal.Copy(entropy, 0, entropyBuffer, entropy.Length);
            var input = new DataBlob { Length = protectedBytes.Length, Data = inputBuffer };
            var optionalEntropy = new DataBlob { Length = entropy.Length, Data = entropyBuffer };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero,
                    IntPtr.Zero, 0x1, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            outputBuffer = output.Data;
            var plaintext = new byte[output.Length];
            Marshal.Copy(output.Data, plaintext, 0, output.Length);
            return plaintext;
        }
        finally
        {
            if (inputBuffer != IntPtr.Zero) Marshal.FreeHGlobal(inputBuffer);
            if (entropyBuffer != IntPtr.Zero) Marshal.FreeHGlobal(entropyBuffer);
            if (outputBuffer != IntPtr.Zero) LocalFree(outputBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
