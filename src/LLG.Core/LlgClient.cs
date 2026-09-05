using System.Net;
using System.Text;

namespace LLG.Core;

public sealed class LlgClient : IDisposable
{
    public static readonly Uri ServiceOrigin = new("https://llgapp.bwespv.com/");
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public LlgClient(HttpClient? httpClient = null)
    {
        ownsClient = httpClient is null;
        client = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });
        client.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<SubscriptionSnapshot> DownloadAsync(UserCredentials credentials, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!credentials.IsComplete) throw new UserVisibleException("请先在设置中填写用户名和密码。");
        try
        {
            progress?.Report("正在登录…");
            using var login = Request(HttpMethod.Post, "/backend/auth/login");
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(credentials.Username.Trim(), Encoding.UTF8), "username");
            form.Add(new StringContent(credentials.Password, Encoding.UTF8), "password");
            login.Content = form;
            var token = ResponseDecoder.ReadToken(ResponseDecoder.Unwrap(await SendAsync(login, cancellationToken), true));
            progress?.Report("正在获取服务器列表…");
            using var sub = Request(HttpMethod.Get, "/backend/user/sub");
            // LLG uses its token verbatim; adding a Bearer prefix breaks authentication.
            sub.Headers.TryAddWithoutValidation("Authorization", token);
            var yaml = ResponseDecoder.ReadYaml(ResponseDecoder.Unwrap(await SendAsync(sub, cancellationToken), false));
            progress?.Report("正在校验服务器列表…");
            NodeConverter.Convert(yaml);
            return new SubscriptionSnapshot { Username = credentials.Username, Yaml = yaml, UpdatedAt = DateTimeOffset.Now };
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new UserVisibleException("连接超时，请检查网络后重试。上次成功的列表仍然保留。", error); }
        catch (HttpRequestException error)
        { throw new UserVisibleException("无法连接服务器，请检查网络后重试。上次成功的列表仍然保留。", error); }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(ServiceOrigin, path));
        request.Headers.TryAddWithoutValidation("ua-v", "1.2.15");
        request.Headers.TryAddWithoutValidation("ua-c", "4");
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UserVisibleException($"服务器暂时不可用（HTTP {(int)response.StatusCode}），请稍后重试。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new UserVisibleException("服务器返回的数据过大，无法处理。");
        // A linked deadline also covers reading the body after ResponseHeadersRead.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int length;
        while ((length = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (output.Length + length > MaxResponseBytes) throw new UserVisibleException("服务器返回的数据过大，无法处理。");
            output.Write(buffer, 0, length);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }
}
