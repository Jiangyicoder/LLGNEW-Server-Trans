using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLG.Core;

public static class ResponseDecoder
{
    // Compatibility constant recovered from the LLG 1.2.15 client, not an account token.
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("ZecvsmbWf".PadRight(32, '\0'));

    public static string Unwrap(string response, bool login)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number))
                throw new UserVisibleException("服务器返回了无法识别的数据，请稍后重试。");
            if (number != 200)
            {
                if (number == 401)
                    throw new UserVisibleException(login ? "登录失败，请检查用户名和密码。" : "登录已失效，请重新更新服务器列表。");
                // Do not display arbitrary response text, which can include private data or HTML.
                throw new UserVisibleException(login ? $"登录未成功，请检查账号信息（代码 {number}）。" : $"暂时无法获取服务器列表（代码 {number}）。");
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new UserVisibleException("服务器返回的数据为空。");
            var text = data.ValueKind == JsonValueKind.String ? data.GetString()! : data.GetRawText();
            if (data.ValueKind == JsonValueKind.String)
                text = DecodeIfEncrypted(text);
            return text;
        }
        catch (JsonException error)
        {
            throw new UserVisibleException("服务器返回了无法识别的数据，请稍后重试。", error);
        }
    }

    public static string DecodeIfEncrypted(string text)
    {
        byte[]? bytes = null;
        byte[]? plaintext = null;
        try
        {
            bytes = Convert.FromBase64String(text);
            if (bytes.Length < 32 || (bytes.Length - 16) % 16 != 0) return text;
            using var aes = Aes.Create();
            aes.Key = Key;
            plaintext = aes.DecryptCbc(bytes.AsSpan(16), bytes.AsSpan(0, 16), PaddingMode.PKCS7);
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or DecoderFallbackException)
        {
            // The original client also accepts an unencrypted successful payload.
            return text;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static string ReadToken(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
            {
                var result = token.GetString()!.Trim();
                if (result.Length > 0 && !result.Any(char.IsControl)) return result;
            }
        }
        catch (JsonException) { }
        throw new UserVisibleException("登录响应中没有有效的认证信息，请稍后重试。");
    }

    public static string ReadYaml(string data)
    {
        var value = data.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (value.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? ""; }
            catch (JsonException) { }
        }
        return data;
    }
}
