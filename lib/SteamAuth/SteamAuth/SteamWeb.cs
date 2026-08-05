using System;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SteamAuth
{
    public class SteamWebResponse
    {
        public string Body { get; set; }
        public WebHeaderCollection Headers { get; set; }
    }

    public class SteamWeb
    {
        public static string MOBILE_APP_USER_AGENT = "Dalvik/2.1.0 (Linux; U; Android 9; Valve Steam App Version/3)";

        public static async Task<string> GETRequest(string url, CookieContainer cookies)
        {
            SteamWebResponse response = await GETRequestWithHeaders(url, cookies);
            return response.Body;
        }

        public static async Task<SteamWebResponse> GETRequestWithHeaders(string url, CookieContainer cookies)
        {
            using (CookieAwareWebClient wc = new CookieAwareWebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.CookieContainer = cookies;
                wc.Headers[HttpRequestHeader.UserAgent] = SteamWeb.MOBILE_APP_USER_AGENT;
                string response = await wc.DownloadStringTaskAsync(url);
                return new SteamWebResponse()
                {
                    Body = response,
                    Headers = wc.ResponseHeaders
                };
            }
        }

        public static async Task<string> POSTRequest(string url, CookieContainer cookies, NameValueCollection body, string headerToReturn = null)
        {
            if (body == null)
                body = new NameValueCollection();

            string response;
            using (CookieAwareWebClient wc = new CookieAwareWebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.CookieContainer = cookies;
                wc.Headers[HttpRequestHeader.UserAgent] = SteamWeb.MOBILE_APP_USER_AGENT;
                byte[] result = await wc.UploadValuesTaskAsync(new Uri(url), "POST", body);
                
                if (!string.IsNullOrEmpty(headerToReturn) && wc.ResponseHeaders != null)
                {
                    return wc.ResponseHeaders[headerToReturn];
                }

                response = Encoding.UTF8.GetString(result);
            }
            return response;
        }

        public static async Task<SteamWebResponse> POSTRequestWithHeaders(string url, CookieContainer cookies, NameValueCollection body)
        {
            if (body == null)
                body = new NameValueCollection();

            using (CookieAwareWebClient wc = new CookieAwareWebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.CookieContainer = cookies;
                wc.Headers[HttpRequestHeader.UserAgent] = SteamWeb.MOBILE_APP_USER_AGENT;
                byte[] result = await wc.UploadValuesTaskAsync(new Uri(url), "POST", body);
                return new SteamWebResponse()
                {
                    Body = Encoding.UTF8.GetString(result),
                    Headers = wc.ResponseHeaders
                };
            }
        }
    }
}
