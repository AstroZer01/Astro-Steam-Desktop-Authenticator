using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Steam_Desktop_Authenticator
{
    internal static class WebViewEnvironmentProvider
    {
        private static readonly Lazy<Task<CoreWebView2Environment>> sharedEnvironment =
            new Lazy<Task<CoreWebView2Environment>>(CreateAsync);

        public static Task<CoreWebView2Environment> GetAsync()
        {
            return sharedEnvironment.Value;
        }

        private static Task<CoreWebView2Environment> CreateAsync()
        {
            Directory.CreateDirectory(ApplicationPaths.WebViewUserDataDirectory);
            return CoreWebView2Environment.CreateAsync(null, ApplicationPaths.WebViewUserDataDirectory);
        }
    }
}
