using Microsoft.Owin;
using Owin;
using System.Threading.Tasks;

namespace InterceptNuGet
{
    public class Startup
    {
        //string Source = "http://nuget3.blob.core.windows.net/preview";
        string Source = "https://www.nuget.org";

        //TODO: currently this code expects a source as a host name because it appends the full path.

        InterceptDispatcher _dispatcher;

        public void Configuration(IAppBuilder app)
        {
            _dispatcher = new InterceptDispatcher(Source);
            app.Run(Invoke);
        }

        public Task Invoke(IOwinContext context)
        {
            return _dispatcher.Invoke(new OwinInterceptCallContext(context));
        }
    }
}
