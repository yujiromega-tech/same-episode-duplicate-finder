using System;
using System.Net;

namespace SameEpisodeDuplicateFinder
{
    internal sealed class HttpTimeoutWebClient : WebClient
    {
        private readonly int timeoutMilliseconds;

        public HttpTimeoutWebClient()
            : this(30000)
        {
        }

        public HttpTimeoutWebClient(int timeoutMilliseconds)
        {
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            if (request != null)
            {
                request.Timeout = timeoutMilliseconds;
            }

            return request;
        }
    }
}
