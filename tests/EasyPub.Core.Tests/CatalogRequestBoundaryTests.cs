using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class CatalogRequestBoundaryTests
{
    [Theory]
    [InlineData("https://example.com/", "www.qidian.com")]
    [InlineData("https://qidian.com.example.com/", "www.qidian.com")]
    [InlineData("http://www.qidian.com/", "www.qidian.com")]
    public void Cookies_do_not_follow_cross_host_or_unencrypted_requests(string url,string origin)
        => Assert.Null(ReferenceCatalogClient.CookieFor(new Uri(url),"test-cookie",origin));

    [Fact]
    public void Explicit_cookie_stays_on_its_origin()
        => Assert.Equal("test-cookie",ReferenceCatalogClient.CookieFor(new Uri("https://www.qidian.com/book/1/"),"test-cookie","www.qidian.com"));

    [Theory]
    [InlineData("http://localhost./")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:192.168.1.1]/")]
    [InlineData("http://[::]/")]
    public void Alternate_local_address_forms_are_rejected(string url)
        => Assert.False(ReferenceCatalogClient.Supported(new Uri(url)));
}
