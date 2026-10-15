using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
namespace Countflow.Modules.Webhooks.Transport;

public sealed record WebhookReply(int Status, string Body);
public interface IWebhookTransport
{
    Task<WebhookReply> SendAsync(string url, string body, string secret, Guid deliveryId, string eventType, CancellationToken cancellationToken);
}
public sealed class ReceiverPolicy(IConfiguration configuration)
{
    public bool IsExplicitlyAllowed(Uri uri) => (configuration.GetSection("Webhooks:AllowedOrigins").Get<string[]>() ?? [])
        .Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
    public bool Accepts(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0) return false;
        if (IsExplicitlyAllowed(uri)) return uri.Scheme is "https" or "http";
        return uri.Scheme == "https" && (!IPAddress.TryParse(uri.Host, out var ip) || IsPublic(ip));
    }
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 198 && b[1] is 18 or 19)
                && !(b[0] == 192 && b[1] == 0) && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
                && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        return b[0] is >= 0x20 and <= 0x3f && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
    }
}
public sealed class WebhookTransport(ReceiverPolicy policy) : IWebhookTransport
{
    public async Task<WebhookReply> SendAsync(string url, string body, string secret, Guid deliveryId, string eventType, CancellationToken cancellationToken)
    {
        if (!policy.Accepts(url)) throw new InvalidOperationException("webhooks.receiver_invalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var sendToken = deadline.Token;
        var uri = new Uri(url);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || (!policy.IsExplicitlyAllowed(uri) && addresses.Any(ip => !ReceiverPolicy.IsPublic(ip))))
                    throw new HttpRequestException("webhooks.receiver_invalid");
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Countflow-Signature", WebhookSecrets.Sign(body, secret));
        request.Headers.Add("X-Countflow-Delivery", deliveryId.ToString()); request.Headers.Add("X-Countflow-Event", eventType);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendToken);
        if (eventType != "webhook.verify") return new((int)response.StatusCode, "");
        await using var stream = await response.Content.ReadAsStreamAsync(sendToken);
        var buffer = new byte[513]; var count = 0;
        while (count < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(count), sendToken); if (read == 0) break; count += read; }
        return new((int)response.StatusCode, Encoding.UTF8.GetString(buffer, 0, count));
    }
}
