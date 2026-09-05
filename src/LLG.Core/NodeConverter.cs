using System.Globalization;
using System.Net;
using YamlDotNet.RepresentationModel;

namespace LLG.Core;

public static class NodeConverter
{
    private static readonly HashSet<string> Common = ["name", "type", "server", "port", "udp"];
    private static readonly HashSet<string> Vless = ["uuid", "alterId", "cipher", "flow", "encryption", "tls", "network"];
    private static readonly HashSet<string> AnyTls = ["password", "sni", "skip-cert-verify"];

    public static ConversionResult Convert(string yaml)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > 8 * 1024 * 1024)
                throw new UserVisibleException("服务器列表为空或数据过大，请重新更新。");
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root ||
                !root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxies) || proxies is not YamlSequenceNode nodes || nodes.Children.Count == 0)
                throw new UserVisibleException("返回的数据中没有可用的服务器列表，请重新更新。");
            if (nodes.Children.Count > 5000) throw new UserVisibleException("节点数量超过支持范围，无法转换。");

            List<string> links = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            var vless = 0;
            var anyTls = 0;
            for (var index = 0; index < nodes.Children.Count; index++)
            {
                if (nodes.Children[index] is not YamlMappingNode mapping) throw Bad(index, "节点格式不正确");
                var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode key || string.IsNullOrEmpty(key.Value) || pair.Value is not YamlScalarNode value)
                        throw Bad(index, "包含暂不支持的复杂参数");
                    if (!fields.TryAdd(key.Value, value.Value)) throw Bad(index, "包含重复参数");
                }
                string Get(string key, string fallback = "") => fields.TryGetValue(key, out var value) ? value ?? "" : fallback;
                bool Bool(string key, bool fallback)
                {
                    if (!fields.ContainsKey(key)) return fallback;
                    if (bool.TryParse(Get(key), out var result)) return result;
                    throw Bad(index, "布尔参数格式不正确");
                }
                var kind = Get("type");
                var allowed = kind switch { "vless" => Vless, "anytls" => AnyTls, _ => throw Bad(index, "协议暂不支持，未复制任何不完整结果") };
                if (fields.Keys.Any(k => !Common.Contains(k) && !allowed.Contains(k))) throw Bad(index, "含有新的连接参数，需要更新转换器");
                if (!Bool("udp", true)) throw Bad(index, "当前转换规则要求节点支持 UDP");
                var name = Get("name");
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw Bad(index, "节点名为空或重复");
                var host = NormalizeHost(Get("server"), index);
                if (!int.TryParse(Get("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                    throw Bad(index, "端口不正确");

                string credential;
                var query = new List<KeyValuePair<string, string>>();
                if (kind == "vless")
                {
                    credential = Get("uuid");
                    if (!Guid.TryParse(credential, out _)) throw Bad(index, "VLESS 标识不正确");
                    if (Bool("tls", false) || Get("network", "tcp") != "tcp") throw Bad(index, "VLESS 传输方式暂不支持");
                    if (Get("flow") is not ("" or "null" or "~") || Get("encryption", "none") != "none") throw Bad(index, "VLESS 加密参数暂不支持");
                    if (Get("alterId", "0") != "0" || Get("cipher", "auto") != "auto") throw Bad(index, "VLESS 兼容参数发生变化");
                    query.Add(new("encryption", "none"));
                    query.Add(new("security", "none"));
                    vless++;
                }
                else
                {
                    credential = Get("password");
                    query.Add(new("security", "tls"));
                    var sni = Get("sni");
                    if (!string.IsNullOrEmpty(sni)) query.Add(new("sni", sni));
                    if (Bool("skip-cert-verify", false)) query.Add(new("insecure", "1"));
                    anyTls++;
                }
                if (string.IsNullOrEmpty(credential) || credential != credential.Trim() || credential.Any(char.IsControl)) throw Bad(index, "连接凭据不正确");
                query.Add(new("type", "tcp"));
                var parameters = string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
                links.Add($"{kind}://{Uri.EscapeDataString(credential)}@{host}:{port}?{parameters}#{Uri.EscapeDataString(name)}");
            }
            if (links.Distinct(StringComparer.Ordinal).Count() != links.Count) throw new UserVisibleException("服务器列表存在重复项，未复制任何不完整结果。");
            return new(string.Join('\n', links) + "\n", links.Count, vless, anyTls);
        }
        catch (YamlDotNet.Core.YamlException error)
        { throw new UserVisibleException("服务器列表无法解析，请重新更新。", error); }
    }

    private static string NormalizeHost(string value, int index)
    {
        var host = value.Trim();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/?#@:\\[]".Contains(c))) throw Bad(index, "服务器地址不正确");
        try
        {
            var ascii = new IdnMapping().GetAscii(host).ToLowerInvariant();
            if (Uri.CheckHostName(ascii) != UriHostNameType.Dns) throw Bad(index, "服务器地址不正确");
            return ascii;
        }
        catch (ArgumentException) { throw Bad(index, "服务器地址不正确"); }
    }

    private static UserVisibleException Bad(int zeroBasedIndex, string reason) => new($"第 {zeroBasedIndex + 1} 个节点{reason}。");
}
