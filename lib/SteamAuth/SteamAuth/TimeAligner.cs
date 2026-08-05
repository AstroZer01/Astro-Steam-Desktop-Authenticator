using System;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using System.Text;
using System.Threading;

namespace SteamAuth
{
    /// <summary>
    /// Class to help align system time with the Steam server time. Not super advanced; probably not taking some things into account that it should.
    /// Necessary to generate up-to-date codes. In general, this will have an error of less than a second, assuming Steam is operational.
    /// </summary>
    public class TimeAligner
    {
        private static bool _aligned = false;
        private static int _timeDifference = 0;
        private static readonly SemaphoreSlim _alignmentLock = new SemaphoreSlim(1, 1);

        public static long GetSteamTime()
        {
            if (!TimeAligner._aligned)
            {
                TimeAligner.AlignTime();
            }
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
        }

        public static async Task<long> GetSteamTimeAsync()
        {
            if (!TimeAligner._aligned)
            {
                await TimeAligner.AlignTimeAsync();
            }
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
        }

        public static void AlignTime()
        {
            long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (WebClient client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                try
                {
                    string response = client.UploadString(APIEndpoints.TWO_FACTOR_TIME_QUERY, "steamid=0");
                    TimeQuery query = JsonConvert.DeserializeObject<TimeQuery>(response);
                    if (query?.Response == null)
                        return;
                    TimeAligner._timeDifference = (int)(query.Response.ServerTime - currentTime);
                    TimeAligner._aligned = true;
                }
                catch (WebException)
                {
                    return;
                }
            }
        }

        public static async Task AlignTimeAsync()
        {
            await _alignmentLock.WaitAsync();
            try
            {
                if (_aligned)
                    return;

                long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using (WebClient client = new WebClient())
                {
                    client.Encoding = Encoding.UTF8;
                    string response = await client.UploadStringTaskAsync(new Uri(APIEndpoints.TWO_FACTOR_TIME_QUERY), "steamid=0");
                    TimeQuery query = JsonConvert.DeserializeObject<TimeQuery>(response);
                    if (query?.Response == null)
                        return;
                    TimeAligner._timeDifference = (int)(query.Response.ServerTime - currentTime);
                    TimeAligner._aligned = true;
                }
            }
            catch (WebException)
            {
                return;
            }
            finally
            {
                _alignmentLock.Release();
            }
        }

        internal class TimeQuery
        {
            [JsonProperty("response")]
            internal TimeQueryResponse Response { get; set; }

            internal class TimeQueryResponse
            {
                [JsonProperty("server_time")]
                public long ServerTime { get; set; }
            }

        }
    }
}
