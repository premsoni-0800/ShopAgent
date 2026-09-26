using System.Runtime.Versioning;
using PrintlyAgent.Ui;
using Xunit;

namespace PrintlyAgent.Tests;

/// <summary>
/// The local UI's port, its caching, and the bridge surface.
///
/// Port of WebUiPortTest.kt, StaticCacheTest.kt and BridgeSurfaceTest.kt. Each
/// of these pins a bug that shipped: a moving port that signed the shop out on
/// every launch, a stale index.html that pinned the dashboard to an old build,
/// and two hand-maintained method lists that drifted apart in silence.
/// </summary>
[SupportedOSPlatform("windows")]
public class WebUiServerTests
{
    // --- the port ------------------------------------------------------------

    [Fact(DisplayName = "the default port sits below the ephemeral range Windows allocates from")]
    public void TheDefaultPortSitsBelowTheEphemeralRange()
    {
        // Anything from 49152 up can be handed to an unrelated outbound socket
        // between two launches, which would silently cost a session.
        Assert.InRange(WebUiServer.DefaultPort, 1024, 49151);
    }

    // --- caching -------------------------------------------------------------

    [Fact(DisplayName = "index html is never kept, so a rebuild is always picked up")]
    public void IndexHtmlIsNeverKept()
    {
        Assert.Equal("no-store", WebUiServer.CacheControlFor("/dashboard/index.html"));
        Assert.Equal("no-store", WebUiServer.CacheControlFor("/webui/index.html"));
        Assert.Equal("no-store", WebUiServer.CacheControlFor("/"));
    }

    [Fact(DisplayName = "hashed bundles are kept forever, because a change renames them")]
    public void HashedBundlesAreKeptForever()
    {
        foreach (var path in new[] { "/assets/index-DSU4dTIU.js", "/dashboard/assets/index-CvL6FBjp.css" })
        {
            var value = WebUiServer.CacheControlFor(path);
            Assert.Contains("immutable", value);
            Assert.Contains("max-age=", value);
        }
    }

    [Fact(DisplayName = "anything without a content hash defaults to not being kept")]
    public void AnythingWithoutAContentHashIsNotKept()
    {
        // favicon.svg and icons.svg keep their names across builds, so the safe
        // answer for them is the same as for index.html.
        foreach (var path in new[] { "/favicon.svg", "/icons.svg", "/bridge.js" })
        {
            Assert.Equal("no-store", WebUiServer.CacheControlFor(path));
        }
    }

    [Fact(DisplayName = "a served file is labelled with a type the webview will render")]
    public void AServedFileIsLabelledWithAUsableType()
    {
        // A wrong type here does not fail loudly: the page renders as text, or
        // the script never executes and the screen is simply blank.
        Assert.StartsWith("text/html", WebUiServer.ContentTypeFor("/dashboard/index.html"));
        Assert.StartsWith("application/javascript", WebUiServer.ContentTypeFor("/assets/app.js"));
        Assert.StartsWith("text/css", WebUiServer.ContentTypeFor("/assets/app.css"));
        Assert.Equal("image/svg+xml", WebUiServer.ContentTypeFor("/favicon.svg"));
    }

    // --- the bridge surface --------------------------------------------------

    /// <summary>
    /// The bridge is described in two places and both have to agree.
    ///
    /// JsBridge.InvokeAsync decides what the agent will answer; the shim decides
    /// what the page can even call. In the Kotlin these were two hand-written
    /// lists, and they drifted - open_external was added to the dispatcher and
    /// left out of the shim, so it was `undefined` on the page. The call threw a
    /// TypeError, the page's own wrapper caught it and returned null, and every
    /// caller read that as "the desktop could not answer". The feature did
    /// nothing and said nothing.
    ///
    /// Here the shim is generated from the same array, so the classes of bug is
    /// gone - but the test stays, because generation is itself something that
    /// can be undone by a well-meaning edit.
    /// </summary>
    [Fact(DisplayName = "every method the agent answers is callable from the page")]
    public void EveryMethodTheAgentAnswersIsCallableFromThePage()
    {
        var shim = ShimScript.Source;
        foreach (var method in JsBridge.Methods)
        {
            Assert.Contains($"'{method}'", shim);
        }
    }

    [Fact(DisplayName = "open_external and print_document are both on the bridge")]
    public void OpenExternalAndPrintDocumentAreOnTheBridge()
    {
        // Named explicitly because these two are the ones that drifted, and the
        // preview and the manual print path both stop working without them.
        Assert.Contains("open_external", JsBridge.Methods);
        Assert.Contains("print_document", JsBridge.Methods);
    }

    [Fact(DisplayName = "the bridge lists no method twice")]
    public void TheBridgeListsNoMethodTwice()
    {
        Assert.Equal(JsBridge.Methods.Distinct().Count(), JsBridge.Methods.Count);
    }

    [Fact(DisplayName = "the shim announces itself, so a page waiting for the bridge is released")]
    public void TheShimAnnouncesItself()
    {
        // whenReady() in the page waits for this event and otherwise gives up
        // after five seconds, leaving the agent panel hidden for the life of the
        // window.
        Assert.Contains("pywebviewready", ShimScript.Source);
        Assert.Contains("window.pywebview = { api: api }", ShimScript.Source);
    }

    [Fact(DisplayName = "the shim installs itself only once")]
    public void TheShimInstallsItselfOnlyOnce()
    {
        // Re-injected on a second navigation it would replace the pending map,
        // stranding every in-flight call.
        Assert.Contains("if (window.pywebview) return;", ShimScript.Source);
    }
}
