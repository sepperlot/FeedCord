using System.Globalization;
using FeedCord.Common;
using FeedCord.Infrastructure.Http;
using FeedCord.Services.Interfaces;
using System.Xml.Linq;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace FeedCord.Infrastructure.Parsers
{
    public class YoutubeParsingService : IYoutubeParsingService
    {
        private readonly ICustomHttpClient _httpClient;
        private readonly ILogger<YoutubeParsingService> _logger;
        public YoutubeParsingService(ICustomHttpClient httpClient, ILogger<YoutubeParsingService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<List<Post?>> GetXmlUrlAndFeed(string xml)
        {
            if (xml.StartsWith("https") && xml.Contains("xml"))
            {
                return await GetRecentPosts(xml);
            }
                

            var doc = new HtmlDocument();
            doc.LoadHtml(xml);

            var node = doc.DocumentNode.SelectSingleNode("//link[@rel='alternate' and @type='application/rss+xml']");

            if (node != null)
            {
                var hrefValue = node.GetAttributeValue("href", "");
                return await GetRecentPosts(hrefValue);
            }

            _logger.LogWarning("No RSS feed link found in the provided XML.");
            return new List<Post?>();
        }

        private async Task<List<Post?>> GetRecentPosts(string xmlUrl)
        {
            if (string.IsNullOrEmpty(xmlUrl))
            {
                return new List<Post?>();
            }

            try
            {
                var response = await _httpClient.GetAsyncWithFallback(xmlUrl);

                if (response is null) return new List<Post?>();
                
                response.EnsureSuccessStatusCode();

                var xmlContent = await response.Content.ReadAsStringAsync();

                var xdoc = XDocument.Parse(xmlContent);
                if (xdoc.Root == null) return new List<Post?>();

                XNamespace atomNs = "http://www.w3.org/2005/Atom";
                XNamespace mediaNs = "http://search.yahoo.com/mrss/";

                var channelTitle = xdoc.Root.Element(atomNs + "title")?.Value ?? string.Empty;

                // NOTE: Elements() (plural) returns every <entry> in the feed, not just
                // the first. Using Element() (singular) here previously meant FeedCord
                // could only ever see the single newest video - if the app was down
                // when multiple videos were uploaded, all but the most recent were
                // silently and permanently skipped, since the watermark advanced past
                // them without ever evaluating them. Returning every entry lets the
                // caller's normal "newer than last seen" filtering catch up properly.
                var videoEntries = xdoc.Root.Elements(atomNs + "entry").ToList();

                if (videoEntries.Count == 0)
                {
                    return new List<Post?>();
                }

                var posts = new List<Post?>();

                foreach (var videoEntry in videoEntries)
                {
                    var videoTitle = videoEntry.Element(atomNs + "title")?.Value ?? string.Empty;
                    var videoLink = videoEntry.Element(atomNs + "link")?.Attribute("href")?.Value ?? string.Empty;
                    var videoThumbnail = videoEntry.Element(mediaNs + "group")?.Element(mediaNs + "thumbnail")?.Attribute("url")?.Value ?? string.Empty;
                    var videoPublished = DateTime.Parse(videoEntry.Element(atomNs + "published")?.Value ?? DateTime.MinValue.ToString(CultureInfo.CurrentCulture));
                    var videoAuthor = videoEntry.Element(atomNs + "author")?.Element(atomNs + "name")?.Value ?? string.Empty;

                    posts.Add(new Post(videoTitle, videoThumbnail, string.Empty, videoLink, channelTitle, videoPublished, videoAuthor, Array.Empty<string>()));
                }

                return posts;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error retrieving RSS feed from URL: {Ex}", ex);
                return new List<Post?>();
            }
        }
    }
}
