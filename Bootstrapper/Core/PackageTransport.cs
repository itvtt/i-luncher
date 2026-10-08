using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ILauncher.Bootstrapper
{
    internal interface IBootstrapperHttpTransport : IDisposable
    {
        Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }

    internal sealed class BootstrapperHttpTransport : IBootstrapperHttpTransport
    {
        private readonly HttpClient _client;

        internal BootstrapperHttpTransport()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            _client = new HttpClient(handler, true);
            _client.Timeout = TimeSpan.FromMinutes(15);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.UserAgent.ParseAdd("i-luncher-bootstrapper/1.0");
            return _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        public void Dispose() { _client.Dispose(); }
    }

    internal sealed class PackageDownloader : IDisposable
    {
        private readonly BootstrapperOptions _options;
        private readonly BootstrapperCatalog _catalog;
        private readonly IBootstrapperCredentialProvider _credentials;
        private readonly IBootstrapperHttpTransport _apiTransport;
        private readonly Func<IBootstrapperHttpTransport> _anonymousTransportFactory;
        private readonly bool _ownsApiTransport;

        internal PackageDownloader(BootstrapperOptions options, BootstrapperCatalog catalog,
            IBootstrapperCredentialProvider credentials, IBootstrapperHttpTransport apiTransport,
            Func<IBootstrapperHttpTransport> anonymousTransportFactory)
        {
            _options = options;
            _catalog = catalog;
            _credentials = credentials;
            _apiTransport = apiTransport;
            _anonymousTransportFactory = anonymousTransportFactory;
            _ownsApiTransport = true;
        }

        public void Dispose()
        {
            if (_ownsApiTransport && _apiTransport != null) _apiTransport.Dispose();
        }

        internal async Task DownloadAsync(string assetName, string expectedHash, long expectedBytes,
            string destination, string appId, Action<string, long, long> progress,
            CancellationToken cancellationToken)
        {
            if (expectedBytes <= 0 || expectedBytes > _options.MaximumPackageBytes)
                throw new InvalidDataException("Package byte size is outside the configured limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (!String.IsNullOrWhiteSpace(_options.PackageMirrorBase))
            {
                await DownloadFromMirrorAsync(assetName, expectedBytes, destination, appId, progress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DownloadFromReleaseAsync(assetName, expectedHash, expectedBytes, destination, appId,
                    progress, cancellationToken).ConfigureAwait(false);
            }

            string actualHash = SafeFiles.Sha256File(destination);
            if (!String.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded package hash does not match the pinned catalog.");
            FileInfo file = new FileInfo(destination);
            if (file.Length != expectedBytes)
                throw new InvalidDataException("Downloaded package size does not match the pinned catalog.");
        }

        private async Task DownloadFromReleaseAsync(string assetName, string expectedHash, long expectedBytes,
            string destination, string appId, Action<string, long, long> progress,
            CancellationToken cancellationToken)
        {
            Uri apiBase = new Uri(_catalog.Source.ApiUrl.TrimEnd('/') + "/", UriKind.Absolute);
            string repository = EscapePathSegment(_catalog.Source.Repository.Split('/')[0]) + "/" +
                                EscapePathSegment(_catalog.Source.Repository.Split('/')[1]);
            string releasePath = "repos/" + repository + "/releases/tags/" + EscapePathSegment(_catalog.Source.TagName);
            Uri releaseUri = new Uri(apiBase, releasePath);
            using (HttpResponseMessage releaseResponse = await SendApiAsync(releaseUri,
                       "application/vnd.github+json", cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(releaseResponse, "Release metadata request", cancellationToken).ConfigureAwait(false);
                string releaseJson = await ReadBoundedTextAsync(releaseResponse.Content,
                    _options.MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
                long assetId;
                string reportedDigest;
                long reportedBytes;
                FindAsset(releaseJson, _catalog.Source.TagName, assetName, out assetId,
                    out reportedDigest, out reportedBytes);
                if (reportedBytes != expectedBytes)
                    throw new InvalidDataException("Release asset size does not match the pinned catalog.");
                if (!String.IsNullOrEmpty(reportedDigest) && !String.Equals(reportedDigest,
                        "sha256:" + expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Release asset digest does not match the pinned catalog.");

                Uri assetUri = new Uri(apiBase, "repos/" + repository + "/releases/assets/" +
                    assetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using (HttpResponseMessage assetResponse = await SendApiAsync(assetUri,
                           "application/octet-stream", cancellationToken).ConfigureAwait(false))
                {
                    if (IsRedirect(assetResponse.StatusCode))
                    {
                        Uri location = assetResponse.Headers.Location;
                        ValidateRedirect(location);
                        using (IBootstrapperHttpTransport anonymous = _anonymousTransportFactory())
                        using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, location))
                        using (HttpResponseMessage downloadResponse = await SendAnonymousAsync(anonymous, request, cancellationToken)
                                   .ConfigureAwait(false))
                        {
                            await EnsureSuccessAsync(downloadResponse, "Package download", cancellationToken).ConfigureAwait(false);
                            await WriteResponseAsync(downloadResponse, destination, expectedBytes, appId,
                                "download", progress, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await EnsureSuccessAsync(assetResponse, "Package download", cancellationToken).ConfigureAwait(false);
                        await WriteResponseAsync(assetResponse, destination, expectedBytes, appId,
                            "download", progress, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task DownloadFromMirrorAsync(string assetName, long expectedBytes, string destination,
            string appId, Action<string, long, long> progress, CancellationToken cancellationToken)
        {
            string mirror = _options.PackageMirrorBase;
            Uri mirrorUri;
            if (Uri.TryCreate(mirror, UriKind.Absolute, out mirrorUri) &&
                (mirrorUri.Scheme == Uri.UriSchemeHttp || mirrorUri.Scheme == Uri.UriSchemeHttps))
            {
                if (mirrorUri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(mirrorUri.UserInfo) ||
                    !String.IsNullOrEmpty(mirrorUri.Query) || !String.IsNullOrEmpty(mirrorUri.Fragment))
                    throw new InvalidDataException("Package mirror must be a trusted HTTPS base URI.");
                string baseText = mirror.EndsWith("/", StringComparison.Ordinal) ? mirror : mirror + "/";
                Uri packageUri = new Uri(new Uri(baseText, UriKind.Absolute), Uri.EscapeDataString(assetName));
                using (IBootstrapperHttpTransport anonymous = _anonymousTransportFactory())
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, packageUri))
                using (HttpResponseMessage response = await SendAnonymousAsync(anonymous, request, cancellationToken).ConfigureAwait(false))
                {
                    if (IsRedirect(response.StatusCode))
                    {
                        Uri location = response.Headers.Location;
                        ValidateRedirect(location);
                        using (HttpRequestMessage redirected = new HttpRequestMessage(HttpMethod.Get, location))
                        using (HttpResponseMessage finalResponse = await SendAnonymousAsync(anonymous, redirected, cancellationToken)
                                   .ConfigureAwait(false))
                        {
                            await EnsureSuccessAsync(finalResponse, "Mirror package download", cancellationToken).ConfigureAwait(false);
                            await WriteResponseAsync(finalResponse, destination, expectedBytes, appId,
                                "download", progress, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await EnsureSuccessAsync(response, "Mirror package download", cancellationToken).ConfigureAwait(false);
                        await WriteResponseAsync(response, destination, expectedBytes, appId,
                            "download", progress, cancellationToken).ConfigureAwait(false);
                    }
                }
                return;
            }

            string localRoot = Path.GetFullPath(mirror);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(localRoot, false);
            string localPackage = SafeFiles.CombineContained(localRoot, assetName, "Package mirror asset");
            if (!File.Exists(localPackage)) throw new FileNotFoundException("Package asset is missing from the configured mirror.");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(localPackage, false);
            using (FileStream input = new FileStream(localPackage, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyBoundedAsync(input, output, expectedBytes, appId, progress, cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
        }

        private async Task<HttpResponseMessage> SendApiAsync(Uri uri, string accept,
            CancellationToken cancellationToken)
        {
            Uri apiOrigin = new Uri(_catalog.Source.ApiUrl, UriKind.Absolute);
            Uri targetOrigin = new Uri(uri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
            if (!SameOrigin(apiOrigin, targetOrigin))
                throw new InvalidDataException("Refusing to send API credentials outside the pinned API origin.");
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            string token;
            try { token = _credentials == null ? null : _credentials.GetToken(apiOrigin); }
            catch
            {
                request.Dispose();
                throw new InvalidDataException("Credential provider failed.");
            }
            if (!String.IsNullOrWhiteSpace(token))
            {
                if (token.Length > 4096 || token.IndexOf('\r') >= 0 || token.IndexOf('\n') >= 0)
                {
                    request.Dispose();
                    throw new InvalidDataException("Credential provider returned an invalid token.");
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            try { return await _apiTransport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw new IOException("The configured package service request failed."); }
            finally { request.Dispose(); }
        }

        private static async Task<HttpResponseMessage> SendAnonymousAsync(IBootstrapperHttpTransport transport,
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try { return await transport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw new IOException("The anonymous package download request failed."); }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode) return;
            await Task.FromResult(0).ConfigureAwait(false);
            throw new IOException(operation + " failed with HTTP status " + (int)response.StatusCode + ".");
        }

        private static async Task<string> ReadBoundedTextAsync(HttpContent content, long maxBytes,
            CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maxBytes)
                throw new InvalidDataException("Release metadata exceeds the configured size limit.");
            using (Stream stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (output.Length > maxBytes - read) throw new InvalidDataException("Release metadata exceeds the configured size limit.");
                    output.Write(buffer, 0, read);
                }
                return new UTF8Encoding(false, true).GetString(output.ToArray());
            }
        }

        private static async Task WriteResponseAsync(HttpResponseMessage response, string destination,
            long expectedBytes, string appId, string phase, Action<string, long, long> progress,
            CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength.HasValue &&
                response.Content.Headers.ContentLength.Value != expectedBytes)
                throw new InvalidDataException("Package content length does not match the pinned catalog.");
            using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyBoundedAsync(input, output, expectedBytes, appId, progress, cancellationToken)
                    .ConfigureAwait(false);
                output.Flush(true);
            }
        }

        private static async Task CopyBoundedAsync(Stream input, Stream output, long maximumBytes,
            string appId, Action<string, long, long> progress, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (copied > maximumBytes - read) throw new InvalidDataException("Package download exceeds its pinned byte size.");
                await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                copied += read;
                if (progress != null) progress(appId, copied, maximumBytes);
            }
            if (copied != maximumBytes) throw new InvalidDataException("Package download is shorter than its pinned byte size.");
        }

        private static void FindAsset(string json, string tagName, string assetName,
            out long assetId, out string digest, out long bytes)
        {
            Dictionary<string, object> release = CatalogParser.Object(StrictJson.Parse(json), "release response");
            object tagValue;
            if (!release.TryGetValue("tag_name", out tagValue) ||
                !String.Equals(tagValue as string, tagName, StringComparison.Ordinal))
                throw new InvalidDataException("Release API returned a different tag.");
            object assetsValue;
            List<object> assets = release.TryGetValue("assets", out assetsValue)
                ? assetsValue as List<object> : null;
            if (assets == null) throw new InvalidDataException("Release API response has no asset list.");
            foreach (object item in assets)
            {
                Dictionary<string, object> asset = CatalogParser.Object(item, "release asset");
                object nameValue;
                if (!asset.TryGetValue("name", out nameValue) ||
                    !String.Equals(nameValue as string, assetName, StringComparison.Ordinal)) continue;
                object idValue;
                object sizeValue;
                if (!asset.TryGetValue("id", out idValue) || !asset.TryGetValue("size", out sizeValue))
                    throw new InvalidDataException("Release API returned incomplete asset metadata.");
                assetId = ParseJsonInteger(idValue, "asset.id");
                bytes = ParseJsonInteger(sizeValue, "asset.size");
                object digestValue;
                digest = asset.TryGetValue("digest", out digestValue) ? digestValue as string : null;
                if (assetId <= 0 || bytes <= 0) throw new InvalidDataException("Release API returned invalid asset metadata.");
                return;
            }
            throw new FileNotFoundException("Required package asset was not found in the pinned release.");
        }

        private static long ParseJsonInteger(object value, string name)
        {
            JsonNumber number = value as JsonNumber;
            long parsed;
            if (number == null || number.Text.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 ||
                !Int64.TryParse(number.Text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed))
                throw new InvalidDataException("Release API returned invalid " + name + ".");
            return parsed;
        }

        private static string EscapePathSegment(string value)
        {
            return Uri.EscapeDataString(value);
        }

        private static bool IsRedirect(HttpStatusCode status)
        {
            int value = (int)status;
            return value == 301 || value == 302 || value == 303 || value == 307 || value == 308;
        }

        private static void ValidateRedirect(Uri location)
        {
            if (location == null || location.Scheme != Uri.UriSchemeHttps ||
                !String.IsNullOrEmpty(location.UserInfo) || !String.IsNullOrEmpty(location.Fragment))
                throw new InvalidDataException("Package download redirect is not a valid HTTPS URL.");
        }

        private static bool SameOrigin(Uri left, Uri right)
        {
            return String.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   String.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
        }
    }
}
