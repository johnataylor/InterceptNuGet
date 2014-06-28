using Microsoft.Owin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InterceptNuGet
{
    public class OwinInterceptCallContext : InterceptCallContext
    {
        IOwinContext _owinContext;

        public OwinInterceptCallContext(IOwinContext owinContext)
        {
            _owinContext = owinContext;
        }

        public override Uri RequestUri
        {
            get
            {
                return _owinContext.Request.Uri;
            }
        }

        public override string ResponseContentType
        {
            get
            { 
                return _owinContext.Response.ContentType;
            }
            set
            {
                _owinContext.Response.ContentType = value;
            }
        }

        public override Task WriteResponseAsync(byte[] data)
        {
            return _owinContext.Response.WriteAsync(data);
        }
    }
}
