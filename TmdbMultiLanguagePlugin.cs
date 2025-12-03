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
        public string PreferredLanguages { get; set; }
        public bool EnableDebugMode { get; set; }

        public PluginConfiguration()
        {
            TmdbApiKey = string.Empty;
            PreferredLanguages = "de,en,null";
            EnableDebugMode = false;
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
                _logger.LogDebug(message, args);
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

            var languageParam = config.PreferredLanguages ?? "de,en,null";
            var mediaType = item is Movie ? "movie" : "tv";
            var url = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/images?api_key={config.TmdbApiKey}&include_image_language={languageParam}";
            
            // Log URL without API key for security
            var safeUrl = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/images?api_key=***&include_image_language={languageParam}";
            LogDebugIfEnabled("[TMDB Multi-Language] Fetching images from TMDB API for {ItemName} (TMDB ID: {TmdbId}, Type: {MediaType}, Languages: {Languages})", 
                itemName, tmdbId, mediaType, languageParam);
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

                // Posters (Primary)
                if (imageData?.Posters != null)
                {
                    var posterCount = imageData.Posters.Count;
                    LogDebugIfEnabled("[TMDB Multi-Language] Found {Count} poster(s) for {ItemName} (TMDB ID: {TmdbId})", 
                        posterCount, itemName, tmdbId);
                    
                    images.AddRange(imageData.Posters.Select(poster => new RemoteImageInfo
                    {
                        Url = TmdbImageBaseUrl + poster.FilePath,
                        Type = ImageType.Primary,
                        ProviderName = Name,
                        Language = poster.Iso639_1,
                        Width = poster.Width,
                        Height = poster.Height,
                        CommunityRating = poster.VoteAverage
                    }));
                    
                    var posterLanguages = string.Join(", ", imageData.Posters.Select(p => p.Iso639_1 ?? "null").Distinct());
                    LogDebugIfEnabled("[TMDB Multi-Language] Poster languages for {ItemName}: {Languages}", itemName, posterLanguages);
                }
                else
                {
                    LogDebugIfEnabled("[TMDB Multi-Language] No posters found for {ItemName} (TMDB ID: {TmdbId})", itemName, tmdbId);
                }

                // Backdrops
                if (imageData?.Backdrops != null)
                {
                    var backdropCount = imageData.Backdrops.Count;
                    LogDebugIfEnabled("[TMDB Multi-Language] Found {Count} backdrop(s) for {ItemName} (TMDB ID: {TmdbId})", 
                        backdropCount, itemName, tmdbId);
                    
                    images.AddRange(imageData.Backdrops.Select(backdrop => new RemoteImageInfo
                    {
                        Url = TmdbImageBaseUrl + backdrop.FilePath,
                        Type = ImageType.Backdrop,
                        ProviderName = Name,
                        Language = backdrop.Iso639_1,
                        Width = backdrop.Width,
                        Height = backdrop.Height,
                        CommunityRating = backdrop.VoteAverage
                    }));
                    
                    var backdropLanguages = string.Join(", ", imageData.Backdrops.Select(b => b.Iso639_1 ?? "null").Distinct());
                    LogDebugIfEnabled("[TMDB Multi-Language] Backdrop languages for {ItemName}: {Languages}", itemName, backdropLanguages);
                }
                else
                {
                    LogDebugIfEnabled("[TMDB Multi-Language] No backdrops found for {ItemName} (TMDB ID: {TmdbId})", itemName, tmdbId);
                }

                // Logos
                if (imageData?.Logos != null)
                {
                    var logoCount = imageData.Logos.Count;
                    LogDebugIfEnabled("[TMDB Multi-Language] Found {Count} logo(s) for {ItemName} (TMDB ID: {TmdbId})", 
                        logoCount, itemName, tmdbId);
                    
                    images.AddRange(imageData.Logos.Select(logo => new RemoteImageInfo
                    {
                        Url = TmdbImageBaseUrl + logo.FilePath,
                        Type = ImageType.Logo,
                        ProviderName = Name,
                        Language = logo.Iso639_1,
                        Width = logo.Width,
                        Height = logo.Height,
                        CommunityRating = logo.VoteAverage
                    }));
                    
                    var logoLanguages = string.Join(", ", imageData.Logos.Select(l => l.Iso639_1 ?? "null").Distinct());
                    LogDebugIfEnabled("[TMDB Multi-Language] Logo languages for {ItemName}: {Languages}", itemName, logoLanguages);
                }
                else
                {
                    LogDebugIfEnabled("[TMDB Multi-Language] No logos found for {ItemName} (TMDB ID: {TmdbId})", itemName, tmdbId);
                }

                LogDebugIfEnabled("[TMDB Multi-Language] Successfully retrieved {TotalCount} image(s) for {ItemName} (TMDB ID: {TmdbId}) - Posters: {PosterCount}, Backdrops: {BackdropCount}, Logos: {LogoCount}", 
                    images.Count, itemName, tmdbId, 
                    imageData?.Posters?.Count ?? 0, 
                    imageData?.Backdrops?.Count ?? 0, 
                    imageData?.Logos?.Count ?? 0);

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