using FeedCord.Common;
using FeedCord.Core.Interfaces;
using FeedCord.Helpers;
using FeedCord.Services.Interfaces;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeedCord.Infrastructure.Workers
{
    public class FeedWorker : BackgroundService
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogAggregator _logAggregator;
        private readonly ILogger<FeedWorker> _logger;
        private readonly IFeedManager _feedManager;
        private readonly INotifier _notifier;

        private readonly bool _persistent;
        private readonly string _id;
        private readonly CronExpression _cronSchedule;
        private bool _isInitialized;
        

        public FeedWorker(
            IHostApplicationLifetime lifetime,
            ILogger<FeedWorker> logger,
            IFeedManager feedManager,
            INotifier notifier,
            Config config,
            ILogAggregator logAggregator)
        {
            _lifetime = lifetime;
            _logger = logger;
            _feedManager = feedManager;
            _notifier = notifier;
            _cronSchedule = CronExpression.Parse(config.CronSchedule);
            _id = config.Id;
            _isInitialized = false;
            _persistent = config.PersistenceOnShutdown;
            _logAggregator = logAggregator;

            logger.LogInformation("{id} Created with schedule \"{Cron}\"",
                _id, config.CronSchedule);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            _lifetime.ApplicationStopping.Register(OnShutdown);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logAggregator.SetStartTime(DateTime.Now);

                try
                {
                    await RunRoutineBackgroundProcessAsync();
                }
                catch (Exception e)
                {
                    _logger.LogCritical("Critical Error in Background Process: {E}", e);
                    throw;
                }

                // Persist after every successful cycle, not only on a graceful
                // shutdown. If the process hangs or is killed ungracefully,
                // OnShutdown never runs and the watermark would otherwise be
                // stuck at whatever the last clean-shutdown save captured -
                // potentially hours or days stale. Saving here means an
                // ungraceful death only ever loses at most one check interval's
                // worth of progress.
                PersistState();

                _logAggregator.SetEndTime(DateTime.Now);

                await _logAggregator.SendToBatchAsync();

                var nextOccurrence = _cronSchedule.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local);

                if (nextOccurrence is null)
                {
                    _logger.LogCritical(
                        "{id}: CronSchedule '{Cron}' has no future occurrence - stopping worker.",
                        _id, _cronSchedule);
                    return;
                }

                var delay = nextOccurrence.Value - DateTime.UtcNow;

                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                await Task.Delay(delay, stoppingToken);
            }
        }

        private async Task RunRoutineBackgroundProcessAsync()
        {
            if (!_isInitialized)
            {
                _logger.LogInformation("{id}: Initializing Url Checks..", _id);
                await _feedManager.InitializeUrlsAsync();
                _isInitialized = true;
            }

            var posts = await _feedManager.CheckForNewPostsAsync();

            if (posts.Count > 0)
            {
                _logger.LogInformation("{id}: Found {PostCount} new posts..", _id, posts.Count);
                await _notifier.SendNotificationsAsync(posts);
            }
        }

        private void OnShutdown()
        {
            PersistState();
        }

        private void PersistState()
        {
            if (!_persistent) return;

            var data = _feedManager.GetAllFeedData();
            SaveDataToCsv(data);
        }

        // Guards against two FeedWorker instances (e.g. two configured Instances)
        // reading/writing feed_dump.csv at the same moment during shutdown.
        private static readonly SemaphoreSlim CsvWriteLock = new(1, 1);

        // Since PersistState runs after every successful cycle, a URL still in
        // any Instance's config gets its row refreshed every check - its
        // LastRunDate is effectively always fresh. A URL removed from config
        // simply stops being refreshed and its timestamp freezes in place. That
        // makes age a reliable, ownership-free signal for "no longer
        // configured anywhere" without needing to track which Instance a row
        // belongs to. Entries untouched for longer than this are pruned.
        private static readonly TimeSpan StaleEntryThreshold = TimeSpan.FromDays(30);

        private void SaveDataToCsv(IReadOnlyDictionary<string, FeedState> data)
        {
            var filePath = Path.Combine(AppContext.BaseDirectory, "feed_dump.csv");

            CsvWriteLock.Wait();
            try
            {
                // Read whatever's already there (including rows written by other
                // Instances) so we only ever update our own URLs' rows instead of
                // blindly overwriting the whole file.
                var existing = CsvReader.LoadReferencePosts(filePath);

                foreach (var (key, value) in data)
                {
                    existing[key] = new ReferencePost
                    {
                        IsYoutube = value.IsYoutube,
                        LastRunDate = DateTime.Now
                    };
                }

                var cutoff = DateTime.Now - StaleEntryThreshold;
                var staleKeys = existing
                    .Where(kv => kv.Value.LastRunDate < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var staleKey in staleKeys)
                {
                    existing.Remove(staleKey);
                }

                if (staleKeys.Count > 0)
                {
                    _logger.LogInformation(
                        "{id}: Removed {Count} stale feed_dump.csv entries not refreshed in over {Days} days: {Urls}",
                        _id, staleKeys.Count, StaleEntryThreshold.TotalDays, string.Join(", ", staleKeys));
                }

                using var writer = new StreamWriter(filePath, append: false);
                foreach (var (key, value) in existing)
                {
                    writer.WriteLine($"{key},{value.IsYoutube},{value.LastRunDate:yyyy-MM-ddTHH:mm:ss}");
                }
            }
            finally
            {
                CsvWriteLock.Release();
            }
        }
    }
}
