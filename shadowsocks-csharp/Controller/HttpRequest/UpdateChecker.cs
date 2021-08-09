using Shadowsocks.Model;
using System;
using UpdateChecker;

namespace Shadowsocks.Controller.HttpRequest
{
    public class UpdateChecker : HttpRequest
    {
        private const string Owner = @"ZBrettonYe";
        private const string Repo = @"OtakuW";

        public string LatestVersionNumber;
        public string LatestVersionUrl;

        public bool Found;

        public event EventHandler NewVersionFound;
        public event EventHandler NewVersionFoundFailed;
        public event EventHandler NewVersionNotFound;

        public const string Name = @"OtakuW";
        public const string Copyright = @"Copyright © OtakuW 2017 - 2021";
        public const string Version = @"1.0.0";

        public const string FullVersion = Version +
#if SelfContained
#if Is64Bit
            @" x64" +
#else
            @" x86" +
#endif
#endif
#if DEBUG
        @" Debug";
#else
        @"";
#endif

        public async void Check(Configuration config, bool notifyNoFound)
        {
            try
            {
                var updater = new GitHubReleasesUpdateChecker(
                    Owner,
                    Repo,
                    config.IsPreRelease,
                    Version);

                var userAgent = config.ProxyUserAgent;
                var proxy = CreateProxy(config);
                using var client = CreateClient(true, proxy, userAgent, config.ConnectTimeout * 1000);

                var res = await updater.CheckAsync(client, default);
                LatestVersionNumber = updater.LatestVersion;
                Found = res;
                if (Found)
                {
                    LatestVersionUrl = updater.LatestVersionUrl;
                    NewVersionFound?.Invoke(this, new EventArgs());
                }
                else
                {
                    if (notifyNoFound)
                    {
                        NewVersionNotFound?.Invoke(this, new EventArgs());
                    }
                }
            }
            catch (Exception e)
            {
                Logging.LogUsefulException(e);
                if (notifyNoFound)
                {
                    NewVersionFoundFailed?.Invoke(this, new EventArgs());
                }
            }
        }
    }
}
