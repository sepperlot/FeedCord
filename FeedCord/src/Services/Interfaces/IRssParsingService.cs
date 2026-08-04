using FeedCord.Common;

namespace FeedCord.Services.Interfaces
{
    public interface IRssParsingService
    {
        Task<List<Post?>> ParseRssFeedAsync(string xmlContent, int trim);
        Task<List<Post?>> ParseYoutubeFeedAsync(string channelUrl);
    }
}
