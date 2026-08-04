using FeedCord.Common;

namespace FeedCord.Services.Interfaces
{
    public interface IYoutubeParsingService
    {
        Task<List<Post?>> GetXmlUrlAndFeed(string url);
    }
}
