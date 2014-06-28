using Microsoft.Owin;
using Owin;
using System.Threading.Tasks;

namespace InterceptNuGet
{
    public class Startup
    {
        static string BaseAddress = "http://nuget3.blob.core.windows.net/feed/resolver";
        static string SearchBaseAddress = "http://nuget-dev-0-search.cloudapp.net/search/query";
        static string PassThroughAddress = "http://nuget.org";

        InterceptDispatcher _dispatcher;

        public void Configuration(IAppBuilder app)
        {
            _dispatcher = new InterceptDispatcher(BaseAddress, SearchBaseAddress, PassThroughAddress);
            app.Run(Invoke);
        }

        public Task Invoke(IOwinContext context)
        {
            return _dispatcher.Invoke(new OwinInterceptCallContext(context));
        }
    }
}
