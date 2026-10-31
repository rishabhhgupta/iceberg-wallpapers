using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lively.Services
{
    /// <summary>
    /// Keeps a local copy of the starred pictures, so the screensaver can show
    /// them without waiting for the internet and without touching any website
    /// while the screen is supposed to be resting.
    ///
    /// The foldering is deliberately simple: the screensaver window lists the
    /// folder every time it changes picture. A download appearing halfway
    /// through therefore shows up on its own, with no cross-thread bookkeeping.
    /// </summary>
    public static class FavouriteScreensaverCache
    {
        private static readonly HttpClient Http = CreateHttp();

        /// <summary>Where the copies live.</summary>
        public static string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Iceberg Wallpaper",
            "screensaver");

        private static readonly string FavouritesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Iceberg Wallpaper",
            "favourites.json");

        public static async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);

                var favourites = ReadFavourites();
                if (favourites.Count == 0)
                {
                    return;
                }

                using var gate = new SemaphoreSlim(4);

                var jobs = favourites.Select(item => EnsureLocalCopyAsync(item, gate, cancellationToken));
                await Task.WhenAll(jobs).ConfigureAwait(false);

                Trim();
            }
            catch (OperationCanceledException)
            {
                // The screensaver was stopped; whatever arrived is still useful.
            }
            catch
            {
                // A missing picture must never stop the screen from being covered.
            }
        }

        private static async Task EnsureLocalCopyAsync(
            FavouriteEntry item,
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(item.LocalPath) && File.Exists(item.LocalPath))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(item.FullUrl)
                || !Uri.TryCreate(item.FullUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            var destination = PathFor(uri);
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                return;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                {
                    return;
                }

                using var response = await Http
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var temporary = destination + ".part";
                await using (var stream = File.Create(temporary))
                {
                    await response.Content.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, destination, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One dead link is not worth a broken screensaver.
            }
            finally
            {
                gate.Release();
            }
        }

        private static string PathFor(Uri uri)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var extension = Path.GetExtension(uri.AbsolutePath);

            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 6)
            {
                extension = ".jpg";
            }

            return Path.Combine(DirectoryPath, hash + extension.ToLowerInvariant());
        }

        /// <summary>Newest sixty pictures; the rest are dropped.</summary>
        private static void Trim()
        {
            try
            {
                var files = new DirectoryInfo(DirectoryPath)
                    .EnumerateFiles()
                    .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(60)
                    .ToList();

                foreach (var file in files)
                {
                    file.Delete();
                }
            }
            catch
            {
                // Housekeeping only.
            }
        }

        private static List<FavouriteEntry> ReadFavourites()
        {
            try
            {
                if (!File.Exists(FavouritesPath))
                {
                    return new List<FavouriteEntry>();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var document = JsonSerializer.Deserialize<FavouritesFile>(File.ReadAllText(FavouritesPath), options);
                return document?.Items ?? new List<FavouriteEntry>();
            }
            catch
            {
                return new List<FavouriteEntry>();
            }
        }

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "IcebergWallpaper/1.0 (+https://icebergwallpaper.app)");
            return client;
        }

        private sealed class FavouritesFile
        {
            public List<FavouriteEntry>? Items { get; set; }
        }

        private sealed class FavouriteEntry
        {
            public string? Id { get; set; }
            public string? FullUrl { get; set; }
            public string? LocalPath { get; set; }
        }
    }
}
