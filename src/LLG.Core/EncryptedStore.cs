using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLG.Core;

public sealed class EncryptedStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LLG.SubscriptionHelper.State.v1");
    private readonly string directory;
    public string StatePath => Path.Combine(directory, "state.dat");

    public EncryptedStore(string? dataDirectory = null)
    {
        directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLG.SubscriptionHelper");
    }

    public async Task<SavedState> LoadAsync()
    {
        if (!File.Exists(StatePath)) return new();
        byte[]? plaintext = null;
        try
        {
            if (new FileInfo(StatePath).Length > 20 * 1024 * 1024) throw new InvalidDataException();
            var encrypted = await File.ReadAllBytesAsync(StatePath);
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var state = JsonSerializer.Deserialize<SavedState>(plaintext) ?? throw new InvalidDataException();
            if (state.Version != 1 || state.Credentials is null || state.Credentials.Username is null || state.Credentials.Password is null)
                throw new InvalidDataException();
            if (state.Snapshot is not null && !string.Equals(state.Snapshot.Username, state.Credentials.Username, StringComparison.Ordinal))
                return new SavedState { Credentials = state.Credentials };
            return state;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { throw new UserVisibleException("无法读取已保存的数据，请在设置中重新保存账号。", error); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task SaveAsync(SavedState state)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(directory);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await file.WriteAsync(encrypted);
                await file.FlushAsync();
            }
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        { throw new UserVisibleException("无法保存数据，请检查当前用户的数据目录是否可写。", error); }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }
}
