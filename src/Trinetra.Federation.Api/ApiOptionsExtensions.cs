using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Trinetra.Federation.Api.Auth;

namespace Trinetra.Federation.Api;

internal static class ApiOptionsExtensions
{
    public static IServiceCollection AddTrinetraOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSystemd();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.RespectNullableAnnotations = true;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
        });

        services.AddOptions<NetworkOptions>()
            .Bind(configuration.GetSection(NetworkOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .Validate(options => { options.Validate(); return true; })
            .ValidateOnStart();
        services.AddOptions<StreamingOptions>()
            .Bind(configuration.GetSection(StreamingOptions.SectionName))
            .ValidateOnStart();

        services.AddHttpClient(StreamingOptions.MediaMtxHttpClientName, (sp, client) =>
        {
            var streaming = sp.GetRequiredService<IOptions<StreamingOptions>>().Value;
            client.BaseAddress = new Uri(streaming.MediaMtxBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        // UseCookies: false — MediaMTX's HLS server sets a Secure-flagged session-continuity
        // cookie on the playlist redirect. A Secure cookie is only ever resent over HTTPS, and
        // this hop (Federation.Api -> MediaMTX) is plain HTTP even in a TLS-terminated
        // deployment (MediaMTX binds loopback-only, see StreamingOptions) — .NET's built-in
        // cookie jar correctly refuses to send it back per spec, which silently breaks every
        // request after the first one for the same HLS session ("works once, then 401/204").
        // StreamSessionEndpoints.ProxyAsync forwards the cookie itself, as a plain header value
        // it doesn't police, instead of relying on HttpClient's spec-strict cookie handling.
        //
        // AllowAutoRedirect: false — MediaMTX's HLS server establishes session continuity on the
        // *first* request to a path with a 302 to the same URL plus `?cookieCheck=1` and a
        // Set-Cookie header (its own cookie, unrelated to the RTSP source). With auto-redirect
        // left on, SocketsHttpHandler follows that 302 internally and ProxyAsync only ever sees
        // the final 200 response — the intermediate Set-Cookie is never relayed to the browser.
        // The browser's *next* request (e.g. hls.js fetching the variant playlist named in the
        // master playlist) then reaches MediaMTX with no cookie, fails the continuity check, and
        // MediaMTX returns 401. Passing the 302 through lets the browser follow it itself and
        // carry the cookie forward, exactly like every other upstream response/header here.
        .ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

        // A second, unrelated named client for a camera's own native HLS/WHEP URL (v1.25) —
        // unlike the MediaMTX client above, this one has no fixed BaseAddress (every camera's
        // native URL points at a different host) and is deliberately not scoped to loopback.
        // Same UseCookies/AllowAutoRedirect reasoning as MediaMTX's client: the proxy relays
        // whatever redirect/cookie the device sends rather than letting the handler absorb it.
        services.AddHttpClient(StreamingOptions.NativeStreamHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

        return services;
    }
}
