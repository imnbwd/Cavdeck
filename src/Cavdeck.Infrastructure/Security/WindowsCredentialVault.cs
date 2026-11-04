using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Cavdeck.Application.Abstractions;

namespace Cavdeck.Infrastructure.Security;

// Generic credentials for the current Windows user. The blob is the secret;
// the user name is the access key id. Neither is written to settings.json.
public sealed class WindowsCredentialVault : ICredentialVault
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public Task SaveAsync(string reference, string accessKeyId, string secretAccessKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretAccessKey);
        if (reference.Length > 256 || accessKeyId.Length > 256 || secretAccessKey.Length > 1024)
            throw new ArgumentException("Credential value is too long.");

        return Task.Run(() => Write(reference, accessKeyId, secretAccessKey), cancellationToken);
    }

    public Task<(string AccessKeyId, string SecretAccessKey)?> ReadAsync(
        string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Task.Run(() => Read(reference), cancellationToken);
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Task.Run(() => Delete(reference), cancellationToken);
    }

    private static void Write(string reference, string accessKeyId, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = reference,
                Comment = "Cavdeck",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = accessKeyId
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    $"Credential Manager could not store the secret ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (bytes.Length > 0)
            {
                var zeros = new byte[bytes.Length];
                Marshal.Copy(zeros, 0, blob, zeros.Length);
            }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    private static (string AccessKeyId, string SecretAccessKey)? Read(string reference)
    {
        if (!CredRead(reference, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound)
                return null;
            throw new InvalidOperationException($"Credential Manager could not read the secret ({error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                var secret = Encoding.UTF8.GetString(bytes);
                return (credential.UserName ?? "", secret);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void Delete(string reference)
    {
        if (CredDelete(reference, Generic, 0))
            return;
        var error = Marshal.GetLastWin32Error();
        if (error == NotFound)
            return;
        throw new InvalidOperationException($"Credential Manager could not delete the secret ({error}).");
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
