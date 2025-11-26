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

namespace Jellyfin.Plugin.TmdbMultiLanguage
{
    // Plugin Configuration
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string TmdbApiKey { get; set; }
        public string PreferredLanguages { get; set; }

        public PluginConfiguration()
        {
            TmdbApiKey = string.Empty;
            PreferredLanguages = "de,en,null";
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
        
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3";
        private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/original";

        public string Name => "TMDB Multi-Language";
        public int Order => 0;

        public TmdbMultiLanguageImageProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
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
            var config = Plugin.Instance?.Configuration;
            
            // API Key Check
            if (string.IsNullOrWhiteSpace(config?.TmdbApiKey))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
            
            if (string.IsNullOrEmpty(tmdbId))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var languageParam = config.PreferredLanguages ?? "de,en,null";

            var mediaType = item is Movie ? "movie" : "tv";
            var url = $"{TmdbBaseUrl}/{mediaType}/{tmdbId}/images?api_key={config.TmdbApiKey}&include_image_language={languageParam}";

            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                var response = await httpClient.GetStringAsync(url, cancellationToken);
                
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
                }

                // Backdrops
                if (imageData?.Backdrops != null)
                {
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
                }

                // Logos
                if (imageData?.Logos != null)
                {
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
                }

                return images;
            }
            catch
            {
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