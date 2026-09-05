using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using LLG.Core;

internal static class Program
{
    private const string SyntheticYaml = """
        proxies:
          - name: '测试 VLESS'
            type: vless
            server: example.com
            port: 443
            uuid: 00000000-0000-4000-8000-000000000001
            alterId: 0
            cipher: auto
            flow: null
            encryption: none
            tls: false
            network: tcp
            udp: true
          - name: '测试 AnyTLS # &'
            type: anytls
            server: '2001:db8::1'
            port: 8443
            password: 'a:p@ss/#?&'
            sni: '*.example.com'
            skip-cert-verify: true
            udp: true
        """;
    private static readonly List<string> Checks = [];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "clipboard-check")
            {
                var actual = Clipboard.GetText();
                var expected = File.ReadAllText(args[1]);
                Require(actual.Replace("\r\n", "\n") == expected.Replace("\r\n", "\n"), "Clipboard content matches all converted nodes");
                Console.WriteLine(JsonSerializer.Serialize(new { passed = true, nodes = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length }));
                return 0;
            }
            if (args.Length > 0 && args[0] == "live")
                LiveAsync(args[1]).GetAwaiter().GetResult();
            else if (args.Length > 0 && args[0] == "saved-check")
                SavedCheckAsync(args[1]).GetAwaiter().GetResult();
            else
                CheckAsync(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("CHECK FAILED: " + error.GetType().Name + ": " + (error is UserVisibleException ? error.Message : error.Message.Split('\n')[0]));
            return 1;
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Checks.Add(label);
    }

    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (UserVisibleException) { Checks.Add(label); return; }
        throw new InvalidOperationException(label);
    }

    private static async Task CheckAsync(string[] args)
    {
        var result = NodeConverter.Convert(SyntheticYaml);
        Require(result.Count == 2 && result.VlessCount == 1 && result.AnyTlsCount == 1, "Both supported protocols converted");
        Require(result.Links.Contains("anytls://a%3Ap%40ss%2F%23%3F%26@[2001:db8::1]:8443?security=tls&sni=%2A.example.com&insecure=1&type=tcp#"), "IPv6, reserved credentials, SNI and certificate flag preserved");
        Require(result.Links.Contains("vless://00000000-0000-4000-8000-000000000001@example.com:443?encryption=none&security=none&type=tcp#"), "VLESS transport and security preserved");
        Reject(() => NodeConverter.Convert(SyntheticYaml.Replace("type: anytls", "type: unknown")), "Unsupported protocol rejects whole result");
        Reject(() => NodeConverter.Convert(SyntheticYaml.Replace("tls: false", "tls: true")), "Unsupported VLESS TLS rejected");
        Reject(() => NodeConverter.Convert(SyntheticYaml.Replace("port: 8443", "port: 70000")), "Invalid port rejected");
        Reject(() => NodeConverter.Convert(SyntheticYaml.Replace("cipher: auto", "cipher: auto\n    extra: value")), "Unknown connection field rejected");
        Reject(() => NodeConverter.Convert("proxies: []"), "Empty list rejected");
        Reject(() => NodeConverter.Convert("proxies: ["), "Malformed YAML rejected");
        Reject(() => ResponseDecoder.Unwrap("{\"code\":401}", true), "Business-level login failure rejected");
        Reject(() => ResponseDecoder.ReadToken("{\"token\":\"\\r\\n\"}"), "Invalid token rejected");
        Require(ResponseDecoder.DecodeIfEncrypted(SyntheticYaml) == SyntheticYaml, "Plaintext successful payload accepted");

        var credentials = new UserCredentials { Username = "test@example.invalid", Password = "synthetic-only-password" };
        var handler = new RecordingHandler(SyntheticYaml);
        using var http = new HttpClient(handler);
        using var client = new LlgClient(http);
        var snapshot = await client.DownloadAsync(credentials);
        Require(handler.Requests == 2 && snapshot.Yaml == SyntheticYaml, "Login, AES unwrap and first-party download complete");
        Require(handler.RequestHeadersCorrect, "Multipart login, original token and app headers verified");

        var root = Path.GetFullPath(args.Length > 0 ? args[0] : "qa");
        Directory.CreateDirectory(root);
        var privateRoot = Path.Combine(root, "private", "store-check-" + Guid.NewGuid().ToString("N"));
        var store = new EncryptedStore(privateRoot);
        var state = new SavedState { Credentials = credentials }.WithSnapshot(snapshot);
        await store.SaveAsync(state);
        var bytes = await File.ReadAllBytesAsync(store.StatePath);
        var raw = Encoding.UTF8.GetString(bytes);
        Require(!raw.Contains(credentials.Password) && !raw.Contains(credentials.Username) && !raw.Contains("proxies:"), "Account and node data encrypted on disk");
        var loaded = await new EncryptedStore(privateRoot).LoadAsync();
        Require(loaded.Credentials.Password == credentials.Password && loaded.Snapshot?.Yaml == SyntheticYaml, "Encrypted state survives a new store instance");
        Require(loaded.WithCredentials(new() { Username = "another@example.invalid", Password = "new-password" }).Snapshot is null, "Changing account clears old account snapshot");
        Require(loaded.WithCredentials(credentials).Snapshot is not null, "Saving same account preserves snapshot");
        bytes[bytes.Length / 2] ^= 1;
        await File.WriteAllBytesAsync(store.StatePath, bytes);
        try { await store.LoadAsync(); throw new InvalidOperationException("Corrupt state was accepted"); }
        catch (UserVisibleException) { Checks.Add("Corrupt encrypted state reported clearly"); }

        using var failureClient = new LlgClient(new HttpClient(new FailureHandler()));
        try { await failureClient.DownloadAsync(credentials); throw new InvalidOperationException("Bad password was accepted"); }
        catch (UserVisibleException) { Checks.Add("Failed login does not proceed to subscription request"); }

        int? fixtureCount = null;
        if (args.Length >= 3)
        {
            var fixture = NodeConverter.Convert(await File.ReadAllTextAsync(args[1]));
            var expected = (await File.ReadAllTextAsync(args[2])).Replace("\r\n", "\n");
            Require(fixture.Links == expected, "Previously verified v2rayN links match byte for byte");
            fixtureCount = fixture.Count;
            Directory.CreateDirectory(Path.Combine(root, "private"));
            await File.WriteAllTextAsync(Path.Combine(root, "private", "fixture-v2rayn.txt"), fixture.Links, new UTF8Encoding(false));
        }
        var report = new { passed = true, checks = Checks, fixtureNodes = fixtureCount, time = DateTimeOffset.Now };
        await File.WriteAllTextAsync(Path.Combine(root, "core-checks.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }

    private static async Task LiveAsync(string dataDirectory)
    {
        // Read authorized test credentials from stdin, never from source or process arguments.
        var input = await Console.In.ReadToEndAsync();
        var credentials = JsonSerializer.Deserialize<UserCredentials>(input) ?? throw new InvalidDataException();
        using var client = new LlgClient();
        var snapshot = await client.DownloadAsync(credentials);
        var result = NodeConverter.Convert(snapshot.Yaml);
        await new EncryptedStore(dataDirectory).SaveAsync(new SavedState { Credentials = credentials }.WithSnapshot(snapshot));
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "live-v2rayn.txt"), result.Links, new UTF8Encoding(false));
        var summary = new { passed = true, nodes = result.Count, vless = result.VlessCount, anyTls = result.AnyTlsCount, source = "/backend/user/sub", authorization = "present, raw token", savedStateEncrypted = true, time = DateTimeOffset.Now };
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "live-check.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary));
    }

    private static async Task SavedCheckAsync(string dataDirectory)
    {
        var state = await new EncryptedStore(dataDirectory).LoadAsync();
        if (!state.Credentials.IsComplete || state.Snapshot is null) throw new InvalidDataException("No complete saved account and snapshot");
        var result = NodeConverter.Convert(state.Snapshot.Yaml);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "saved-v2rayn.txt"), result.Links, new UTF8Encoding(false));
        var summary = new { passed = true, savedAccountPresent = true, nodes = result.Count, vless = result.VlessCount, anyTls = result.AnyTlsCount, updatedAt = state.Snapshot.UpdatedAt, encryptedBytes = new FileInfo(new EncryptedStore(dataDirectory).StatePath).Length };
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "saved-check.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary));
    }

    private sealed class FailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":401,\"message\":\"invalid\"}") });
    }

    private sealed class RecordingHandler(string yaml) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool RequestHeadersCorrect { get; private set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            RequestHeadersCorrect &= request.RequestUri!.Host == "llgapp.bwespv.com" && request.Headers.GetValues("ua-v").Single() == "1.2.15" && request.Headers.GetValues("ua-c").Single() == "4";
            string payload;
            if (Requests == 1)
            {
                RequestHeadersCorrect &= request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/backend/auth/login" && request.Content is MultipartFormDataContent && !request.Headers.Contains("Authorization");
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                RequestHeadersCorrect &= body.Contains("name=username") && body.Contains("name=password") && body.Contains("test@example.invalid") && body.Contains("synthetic-only-password");
                payload = "{\"token\":\"  test-raw-token  \"}";
            }
            else
            {
                RequestHeadersCorrect &= request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/backend/user/sub" && request.Headers.GetValues("Authorization").Single() == "test-raw-token";
                payload = yaml;
            }
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes("ZecvsmbWf".PadRight(32, '\0'));
            var iv = Enumerable.Range(1, 16).Select(n => (byte)n).ToArray();
            var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(payload), iv, PaddingMode.PKCS7);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code = 200, data = System.Convert.ToBase64String(iv.Concat(cipher).ToArray()) })) };
        }
    }
}
