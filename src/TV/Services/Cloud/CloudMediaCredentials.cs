using TV.Models;

namespace TV.Services.Cloud;

internal static class CloudMediaCredentials
{
    internal static bool CanSendCookie(CloudProviderKind provider, Uri destination) =>
        destination.Scheme == Uri.UriSchemeHttps && destination.IsDefaultPort && destination.UserInfo.Length == 0 &&
        provider switch
        {
            CloudProviderKind.UC => Matches(destination, "uc.cn") || Matches(destination, "ucweb.com"),
            CloudProviderKind.Quark => Matches(destination, "quark.cn"),
            CloudProviderKind.Baidu => Matches(destination, "baidu.com") || Matches(destination, "baidupcs.com"),
            _ => false
        };

    private static bool Matches(Uri uri, string domain) =>
        uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
