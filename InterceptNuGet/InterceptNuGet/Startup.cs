using Microsoft.Owin;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using Owin;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace InterceptNuGet
{
    public class Startup
    {
        static string BaseAddress = "http://nuget3.blob.core.windows.net/feed/resolver";
        static string PassThroughAddress = "http://nuget.org";

        InterceptDispatcher _dispatcher;

        public void Configuration(IAppBuilder app)
        {
            _dispatcher = new InterceptDispatcher(BaseAddress, PassThroughAddress);
            app.Run(Invoke);
        }

        public Task Invoke(IOwinContext context)
        {
            return _dispatcher.Invoke(new OwinInterceptCallContext(context));
        }
    }
}
