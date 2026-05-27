using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TmdbMultiLanguage
{
    // Plugin Configuration
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string TmdbApiKey { get; set; }

        // Legacy global setting — kept as fallback so existing installs keep working
        // until the user saves the new per-type fields via the config page.
        public string PreferredLanguages { get; set; }

        public string PrimaryLanguages { get; set; }
        public string BackdropLanguages { get; set; }
        public string LogoLanguages { get; set; }
        public bool IgnoreUnratedEpisodes { get; set; }
        public bool EnableDebugMode { get; set; }

        public PluginConfiguration()
        {
            TmdbApiKey = string.Empty;
            PreferredLanguages = "de,en,null";
            PrimaryLanguages = string.Empty;
            BackdropLanguages = string.Empty;
            LogoLanguages = string.Empty;
            IgnoreUnratedEpisodes = false;
            EnableDebugMode = false;
        }

        public string GetLanguagesFor(ImageType imageType)
        {
            var value = imageType switch
            {
                ImageType.Primary => PrimaryLanguages,
                ImageType.Backdrop => BackdropLanguages,
                ImageType.Logo => LogoLanguages,
                _ => string.Empty,
            };
            return string.IsNullOrWhiteSpace(value) ? PreferredLanguages : value;
        }
    }

    // Main Plugin Class
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "TMDB Multi-Language Images";
        public override Guid Id => Guid.Parse("96afa51e-678e-42ac-b9f6-f3679173a23f");
        public override string Description => "Load images from TMDB with configurable language preferences";
        
        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) 
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }

    // Image Provider
    public class TmdbMultiLanguageImageProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<TmdbMultiLanguageImageProvider> _logger;
        
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
        private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/original";

        public string Name => "TMDB Multi-Language";
        public int Order => 0;

        public TmdbMultiLanguageImageProvider(IHttpClientFactory httpClientFactory, ILogger<TmdbMultiLanguageImageProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private void LogDebugIfEnabled(string message, params object[] args)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableDebugMode == true)
            {
                // Use LogInformation instead of LogDebug so logs are visible in Jellyfin console
                _logger.LogInformation(message, args);
            }
        }

        public bool Supports(BaseItem item)
        {
            return item is Movie || item is Series;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var itemName = item.Name ?? "Unknown";
            var itemType = item is Movie ? "Movie" : item is Series ? "Series" : "Unknown";
            var config = Plugin.Instance?.Configuration;
            
            LogDebugIfEnabled("[TMDB Multi-Language] GetImages called for {ItemType}: {ItemName} (ID: {ItemId})", 
                itemType, itemName, item.Id);
            
            // API Key Check
            if (string.IsNullOrWhiteSpace(config?.TmdbApiKey))
            {
                _logger.LogWarning("[TMDB Multi-Language] API Key is not configured. Skipping image fetch for {ItemName}", itemName);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            
            LogDebugIfEnabled("[TMDB Multi-Language] Retrieved TMDB ID for {ItemName}: {TmdbId}", itemName, tmdbId ?? "null");
            
            if (string.IsNullOrEmpty(tmdbId))
            {
                _logger.LogWarning("[TMDB Multi-Language] No TMDB ID found for {ItemType}: {ItemName}. Cannot fetch images.", 
                    itemType, itemName);
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var primaryPriority = ParseLanguagePriority(config.GetLanguagesFor(ImageType.Primary));
            var backdropPriority = ParseLanguagePriority(config.GetLanguagesFor(ImageType.Backdrop));
            var logoPriority = ParseLanguagePriority(config.GetLanguagesFor(ImageType.Logo));

            // Union of all per-type languages — single API call covers all three lists.
            var languageParam = BuildLanguageQueryParam(primaryPriority, backdropPriority, logoPriority);
            var mediaType = item is Movie ? "movie" : "tv";
            var url = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/images?api_key={config.TmdbApiKey}&include_image_language={languageParam}";

            // Log URL without API key for security
            var safeUrl = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/images?api_key=***&include_image_language={languageParam}";
            LogDebugIfEnabled("[TMDB Multi-Language] Fetching images from TMDB API for {ItemName} (TMDB ID: {TmdbId}, Type: {MediaType}, Languages: {Languages})",
                itemName, tmdbId, mediaType, languageParam);
            LogDebugIfEnabled("[TMDB Multi-Language] Per-type priority - Primary: [{Primary}], Backdrop: [{Backdrop}], Logo: [{Logo}]",
                FormatPriority(primaryPriority), FormatPriority(backdropPriority), FormatPriority(logoPriority));
            LogDebugIfEnabled("[TMDB Multi-Language] API URL: {Url}", safeUrl);

            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                LogDebugIfEnabled("[TMDB Multi-Language] Sending HTTP request to TMDB API for {ItemName} (TMDB ID: {TmdbId})", 
                    itemName, tmdbId);
                
                var httpResponse = await httpClient.GetAsync(url, cancellationToken);
                var statusCode = (int)httpResponse.StatusCode;
                
                LogDebugIfEnabled("[TMDB Multi-Language] Received HTTP response from TMDB API for {ItemName} (TMDB ID: {TmdbId}). Status Code: {StatusCode} {StatusText}", 
                    itemName, tmdbId, statusCode, httpResponse.StatusCode);
                
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var errorContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("[TMDB Multi-Language] TMDB API returned error status {StatusCode} {StatusText} for {ItemName} (TMDB ID: {TmdbId}). Error response: {ErrorResponse}", 
                        statusCode, httpResponse.StatusCode, itemName, tmdbId, errorContent);
                    return Enumerable.Empty<RemoteImageInfo>();
                }
                
                var response = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                
                LogDebugIfEnabled("[TMDB Multi-Language] Successfully received response from TMDB API for {ItemName} (TMDB ID: {TmdbId}). Response length: {Length} bytes", 
                    itemName, tmdbId, response?.Length ?? 0);
                
                // Ensure flexible JSON deserialization
                var jsonOptions = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                };

                var imageData = JsonSerializer.Deserialize<TmdbImageResponse>(response, jsonOptions);
                var images = new List<RemoteImageInfo>();

                var filteredPosters = AddFilteredImages(images, imageData?.Posters, ImageType.Primary, primaryPriority, "poster", itemName, tmdbId);
                var filteredBackdrops = AddFilteredImages(images, imageData?.Backdrops, ImageType.Backdrop, backdropPriority, "backdrop", itemName, tmdbId);
                var filteredLogos = AddFilteredImages(images, imageData?.Logos, ImageType.Logo, logoPriority, "logo", itemName, tmdbId);

                LogDebugIfEnabled("[TMDB Multi-Language] Successfully retrieved {TotalCount} image(s) for {ItemName} (TMDB ID: {TmdbId}) - Posters: {PosterCount}/{PosterTotal}, Backdrops: {BackdropCount}/{BackdropTotal}, Logos: {LogoCount}/{LogoTotal}",
                    images.Count, itemName, tmdbId,
                    filteredPosters, imageData?.Posters?.Count ?? 0,
                    filteredBackdrops, imageData?.Backdrops?.Count ?? 0,
                    filteredLogos, imageData?.Logos?.Count ?? 0);

                return images;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "[TMDB Multi-Language] HTTP error while fetching images for {ItemName} (TMDB ID: {TmdbId}): {Message}", 
                    itemName, tmdbId, ex.Message);
                return Enumerable.Empty<RemoteImageInfo>();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[TMDB Multi-Language] JSON deserialization error for {ItemName} (TMDB ID: {TmdbId}): {Message}", 
                    itemName, tmdbId, ex.Message);
                return Enumerable.Empty<RemoteImageInfo>();
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "[TMDB Multi-Language] Request cancelled for {ItemName} (TMDB ID: {TmdbId})", 
                    itemName, tmdbId);
                return Enumerable.Empty<RemoteImageInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TMDB Multi-Language] Unexpected error while fetching images for {ItemName} (TMDB ID: {TmdbId}): {Message}", 
                    itemName, tmdbId, ex.Message);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var httpClient = _httpClientFactory.CreateClient();
            return httpClient.GetAsync(url, cancellationToken);
        }

        // null entry in the list represents TMDB's "language-less" images (iso_639_1 = null/"").
        private static List<string?> ParseLanguagePriority(string? languages)
        {
            if (string.IsNullOrWhiteSpace(languages))
            {
                return new List<string?>();
            }

            var result = new List<string?>();
            foreach (var raw in languages.Split(','))
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                string? entry = string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
                if (!result.Any(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        private static string BuildLanguageQueryParam(params List<string?>[] priorities)
        {
            var union = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in priorities)
            {
                foreach (var lang in list)
                {
                    var token = lang ?? "null";
                    if (seen.Add(token))
                    {
                        union.Add(token);
                    }
                }
            }
            return union.Count == 0 ? "null" : string.Join(",", union);
        }

        private static string FormatPriority(List<string?> priority)
        {
            return string.Join(",", priority.Select(l => l ?? "null"));
        }

        private static int GetLanguagePriorityIndex(string? imageLanguage, List<string?> priority)
        {
            for (var i = 0; i < priority.Count; i++)
            {
                var p = priority[i];
                if (p is null)
                {
                    if (string.IsNullOrEmpty(imageLanguage))
                    {
                        return i;
                    }
                }
                else if (string.Equals(p, imageLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private int AddFilteredImages(
            List<RemoteImageInfo> target,
            List<TmdbImage>? source,
            ImageType imageType,
            List<string?> priority,
            string typeLabel,
            string itemName,
            string tmdbId)
        {
            if (source == null)
            {
                LogDebugIfEnabled("[TMDB Multi-Language] No {Type}s found for {ItemName} (TMDB ID: {TmdbId})", typeLabel, itemName, tmdbId);
                return 0;
            }

            LogDebugIfEnabled("[TMDB Multi-Language] Found {Count} {Type}(s) for {ItemName} (TMDB ID: {TmdbId}) before filtering",
                source.Count, typeLabel, itemName, tmdbId);

            var ignoreUnrated = Plugin.Instance?.Configuration?.IgnoreUnratedEpisodes == true;

            var ordered = source
                .Where(img => !ignoreUnrated || img.VoteAverage > 0)
                .Select(img => new { Image = img, Index = GetLanguagePriorityIndex(img.Iso639_1, priority) })
                .Where(x => x.Index >= 0)
                .OrderBy(x => x.Index)
                .ThenByDescending(x => x.Image.VoteAverage)
                .Select(x => x.Image)
                .ToList();

            target.AddRange(ordered.Select(img => new RemoteImageInfo
            {
                Url = TmdbImageBaseUrl + img.FilePath,
                Type = imageType,
                ProviderName = Name,
                Language = img.Iso639_1,
                Width = img.Width,
                Height = img.Height,
                CommunityRating = img.VoteAverage
            }));

            var availableLanguages = string.Join(", ", source.Select(i => i.Iso639_1 ?? "null").Distinct());
            LogDebugIfEnabled("[TMDB Multi-Language] {Type} languages available for {ItemName}: {Languages} — kept {Kept} matching priority [{Priority}]",
                typeLabel, itemName, availableLanguages, ordered.Count, FormatPriority(priority));

            return ordered.Count;
        }
    }

    // TMDB API Response Models
    public class TmdbImageResponse
    {
        [JsonPropertyName("posters")]
        public List<TmdbImage>? Posters { get; set; }
        
        [JsonPropertyName("backdrops")]
        public List<TmdbImage>? Backdrops { get; set; }
        
        [JsonPropertyName("logos")]
        public List<TmdbImage>? Logos { get; set; }
    }

    public class TmdbImage
    {
        [JsonPropertyName("file_path")]
        public string FilePath { get; set; } = string.Empty;
        
        [JsonPropertyName("iso_639_1")]
        public string? Iso639_1 { get; set; }
        
        [JsonPropertyName("width")]
        public int Width { get; set; }
        
        [JsonPropertyName("height")]
        public int Height { get; set; }
        
        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }
    }
}