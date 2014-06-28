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
    public class StartupOld
    {
        public void Configuration(IAppBuilder app)
        {
            app.Run(Invoke);
        }

        static string Address = "http://nuget.org";

        ConcurrentDictionary<Uri, Tuple<string, byte[]>> Cache = new ConcurrentDictionary<Uri, Tuple<string, byte[]>>();

        public async Task Invoke(IOwinContext context)
        {
            string path = Uri.UnescapeDataString(context.Request.Uri.AbsolutePath);

            Console.WriteLine(path);

            Tuple<string, Func<IOwinContext, Task>>[] funcs =
            {
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/Search()/$count", Count),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/Search", Search),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/FindPackagesById", FindPackagesById),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/GetUpdates", GetUpdates),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/Packages", Packages),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/package-ids", PackageIds),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/package-versions", PackageVersions),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/$metadata", Metadata),
                new Tuple<string, Func<IOwinContext, Task>>("/api/v2/", Default)
            };

            foreach (var func in funcs)
            {
                if (path.StartsWith(func.Item1))
                {
                    await func.Item2(context);
                    break;
                }
            }
        }

        async Task Default(IOwinContext context)
        {
            if (context.Request.Uri.AbsolutePath == "/api/v2/")
            {
                await Root(context);
            }
            else
            {
                Log("default", ConsoleColor.Red);
                await PassThrough(context);
            }
        }
        async Task Root(IOwinContext context)
        {
            Log("Root", ConsoleColor.Green);
            await PassThrough(context);
        }

        async Task Metadata(IOwinContext context)
        {
            Log("Metadata", ConsoleColor.Green);
            await PassThrough(context);
        }

        async Task Count(IOwinContext context)
        {
            Log("Count", ConsoleColor.Green);
            await PassThrough(context);
        }
        async Task Search(IOwinContext context)
        {
            Log("Search", ConsoleColor.Green);
            await PassThrough(context);
        }

        async Task FindPackagesById(IOwinContext context)
        {
            Log("FindPackagesById", ConsoleColor.Green);

            string query = context.Request.Uri.Query;

            string[] terms = context.Request.Uri.Query.TrimStart('?').Split('&');
            bool isLatestVersion = false;
            bool isAbsoluteLatestVersion = false;
            string id = null;
            foreach (string term in terms)
            {
                if (term.StartsWith("id"))
                {
                    string t = Uri.UnescapeDataString(term);
                    string s = t.Substring(t.IndexOf("=") + 1).Trim(' ', '\'');

                    id = s.ToLowerInvariant();
                }
                else if (term.StartsWith("$filter"))
                {
                    string s = term.Substring(term.IndexOf("=") + 1);

                    isLatestVersion = (s == "IsLatestVersion");

                    isAbsoluteLatestVersion = (s == "IsAbsoluteLatestVersion");
                }
            }
            if (id == null)
            {
                throw new Exception("unable to find id in query string");
            }

            if (isLatestVersion || isAbsoluteLatestVersion)
            {
                await GetLatestVersionPackage(id, context, isAbsoluteLatestVersion);
            }
            else
            {
                await GetAllPackageVersions(id, context);
            }
        }

        async Task PackageIds(IOwinContext context)
        {
            Log("PackageIds", ConsoleColor.Green);

            //  direct this to Lucene

            await PassThrough(context, true);
        }

        async Task PackageVersions(IOwinContext context)
        {
            Log("PackageVersions", ConsoleColor.Green);

            string path = context.Request.Uri.AbsolutePath;
            string id = path.Substring(path.LastIndexOf("/") + 1);

            JObject resolverBlob = await FetchJson(MakeResolverAddress(id));

            List<NuGetVersion> versions = new List<NuGetVersion>();
            foreach (JToken package in resolverBlob["package"])
            {
                versions.Add(NuGetVersion.Parse(package["version"].ToString()));
            }

            versions.Sort();

            JArray array = new JArray();
            foreach (NuGetVersion version in versions)
            {
                array.Add(version.ToString());
            }

            await WriteResponse(context, array);
        }

        async Task GetUpdates(IOwinContext context)
        {
            Log("GetUpdates", ConsoleColor.Green);
            await PassThrough(context);
        }

        async Task Packages(IOwinContext context)
        {
            Log("Packages", ConsoleColor.Green);

            if (context.Request.Uri.AbsolutePath.EndsWith("Packages()") && context.Request.Uri.Query == string.Empty)
            {
                await GetAllPackagesWithFilter(context);
            }
            else
            {
                await GetPackage(context);
            }
        }

        static async Task GetAllPackagesWithFilter(IOwinContext context)
        {
            string[] terms = context.Request.Uri.Query.Split('&');
            string id = null;
            foreach (string term in terms)
            {
                if (term.Trim('?').StartsWith("$filter"))
                {
                    string t = Uri.UnescapeDataString(term);
                    string s = t.Substring(t.IndexOf("eq") + 2).Trim(' ', '\'');

                    id = s.ToLowerInvariant();
                }
            }
            if (id == null)
            {
                throw new Exception("unable to find id in query string");
            }

            await GetAllPackageVersions(id, context);
        }

        async Task GetPackage(IOwinContext context)
        {
            string path = Uri.UnescapeDataString(context.Request.Uri.AbsolutePath);
            string args = path.Substring(path.LastIndexOf('(')).Trim('(',')');

            string id = null;
            string version = null;

            string[] aps = args.Split(',');
            foreach (var ap in aps)
            {
                string[] a = ap.Split('=');
                if (a[0].Trim('\'') == "Id")
                {
                    id = a[1].Trim('\'');
                }
                else if (a[0].Trim('\'') == "Version")
                {
                    version = a[1].Trim('\'');
                }
            }

            await GetPackage(id, version, context);
        }

        static async Task GetPackage(string id, string version, IOwinContext context)
        {
            JObject resolverBlob = await FetchJson(MakeResolverAddress(id));
            
            NuGetVersion desiredVersion = NuGetVersion.Parse(version);
            JToken desiredPackage = null;

            foreach (JToken package in resolverBlob["package"])
            {
                NuGetVersion currentVersion = NuGetVersion.Parse(package["version"].ToString());
                if (currentVersion == desiredVersion)
                {
                    desiredPackage = package;
                    break;
                }
            }

            if (desiredPackage == null)
            {
                throw new Exception(string.Format("unable to find version {0} of package {1}", version, id));
            }

            List<JToken> packages = new List<JToken> { desiredPackage };
            XElement feed = MakeFeed(packages, id);
            await WriteResponse(context, feed);
        }

        static async Task GetLatestVersionPackage(string id, IOwinContext context, bool includePrerelease)
        {
            Log(string.Format("GetLatestVersionPackage: {0} {1}", id, includePrerelease ? "[include prerelease]" : ""), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(MakeResolverAddress(id));
            JToken candidateLatest = resolverBlob["package"].FirstOrDefault();

            if (candidateLatest == null)
            {
                throw new Exception(string.Format("package {0} not found", id));
            }

            NuGetVersion candidateLatestVersion = NuGetVersion.Parse(candidateLatest["version"].ToString());
            foreach (JToken package in resolverBlob["package"])
            {
                NuGetVersion currentVersion = NuGetVersion.Parse(package["version"].ToString());

                if (includePrerelease)
                {
                    if (currentVersion.IsPrerelease && currentVersion > candidateLatestVersion)
                    {
                        candidateLatest = package;
                        candidateLatestVersion = currentVersion;
                    }
                }
                else
                {
                    if (!currentVersion.IsPrerelease && currentVersion > candidateLatestVersion)
                    {
                        candidateLatest = package;
                        candidateLatestVersion = currentVersion;
                    }
                }
            }

            if (candidateLatestVersion.IsPrerelease && !includePrerelease)
            {
                throw new Exception(string.Format("only prerelease versions of package {0} found", id));
            }

            XElement feed = MakeFeed(new List<JToken> { candidateLatest }, id);
            await WriteResponse(context, feed);
        }

        static async Task GetAllPackageVersions(string id, IOwinContext context)
        {
            Log(string.Format("GetAllPackageVersions: {0}", id), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(MakeResolverAddress(id));
            XElement feed = MakeFeed(resolverBlob["package"], id);
            await WriteResponse(context, feed);
        }

        static async Task WriteResponse(IOwinContext context, XElement feed)
        {
            context.Response.ContentType = "application/atom+xml; type=feed; charset=utf-8";

            MemoryStream stream = new MemoryStream();
            XmlWriter writer = XmlWriter.Create(stream);
            feed.WriteTo(writer);
            writer.Flush();
            byte[] data = stream.ToArray();

            await context.Response.WriteAsync(data);
        }

        static async Task WriteResponse(IOwinContext context, JToken jtoken)
        {
            context.Response.ContentType = "application/json; charset=utf-8";

            MemoryStream stream = new MemoryStream();
            TextWriter writer = new StreamWriter(stream);
            writer.Write(jtoken.ToString());
            writer.Flush();
            byte[] data = stream.ToArray();

            await context.Response.WriteAsync(data);
        }

        static async Task<JObject> FetchJson(Uri address)
        {
            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync(address);
            string json = await response.Content.ReadAsStringAsync();
            JObject obj = JObject.Parse(json);
            return obj;
        }

        static Uri MakeResolverAddress(string id)
        {
            id = id.ToLowerInvariant();
            Uri resolverBlobAddress = new Uri(string.Format("http://nuget3.blob.core.windows.net/feed/resolver/{0}.json", id));
            return resolverBlobAddress;
        }

        static XElement MakeFeed(IEnumerable<JToken> packages, string id)
        {
            XNamespace atom = XNamespace.Get(@"http://www.w3.org/2005/Atom");
            XElement feed = new XElement(atom + "feed");
            feed.Add(new XElement(atom + "id", "http://www.nuget.org/api/v2/Packages"));
            foreach (JToken package in packages)
            {
                feed.Add(MakeEntry(id, package));
            }
            return feed;
        }

        static XElement MakeEntry(string id, JToken package)
        {
            XNamespace atom = XNamespace.Get(@"http://www.w3.org/2005/Atom");
            XNamespace d = XNamespace.Get(@"http://schemas.microsoft.com/ado/2007/08/dataservices");
            XNamespace m = XNamespace.Get(@"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");

            XElement entry = new XElement(atom + "entry");

            entry.Add(new XElement(atom + "id", string.Format("http://www.nuget.org/api/v2/Packages(Id='{0}',Version='{1}'", id, package["version"])));
            entry.Add(new XElement(atom + "title", id));
            entry.Add(new XElement(atom + "author", new XElement(atom + "name", "SHIM")));

            entry.Add(new XElement(atom + "content",
                new XAttribute("type", "application/zip"),
                new XAttribute("src", string.Format("http://www.nuget.org/api/v2/package/{0}/{1}", id, package["version"]))));

            XElement properties = new XElement(m + "properties");
            entry.Add(properties);

            properties.Add(new XElement(d + "Version", package["version"].ToString()));
            properties.Add(new XElement(d + "Description", "SHIM"));

            properties.Add(new XElement(d + "IsLatestVersion", new XAttribute(m + "type", "Edm.Boolean"), "true"));
            properties.Add(new XElement(d + "IsAbsoluteLatestVersion", new XAttribute(m + "type", "Edm.Boolean"), "true"));
            properties.Add(new XElement(d + "IsPrerelease", new XAttribute(m + "type", "Edm.Boolean"), "false"));

            JToken dependencies;
            if (((JObject)package).TryGetValue("dependencies", out dependencies))
            {
                StringBuilder sb = new StringBuilder();

                foreach (JToken group in dependencies["group"])
                {
                    string targetFramework = string.Empty;
                    JToken tf;
                    if (((JObject)group).TryGetValue("targetFramework", out tf))
                    {
                        targetFramework = tf.ToString();
                    }

                    foreach (JToken dependency in group["dependency"])
                    {
                        sb.AppendFormat("{0}:{1}:{2}|", dependency["id"].ToString().ToLowerInvariant(), dependency["range"], targetFramework);
                    }

                    if (sb.Length > 0)
                    {
                        sb.Remove(sb.Length - 1, 1);
                    }
                }

                properties.Add(new XElement(d + "Dependencies", sb.ToString()));
            }

            bool license = false;

            properties.Add(new XElement(d + "RequireLicenseAcceptance", new XAttribute(m + "type", "Edm.Boolean"), license.ToString().ToLowerInvariant()));

            if (license)
            {
                properties.Add(new XElement(d + "LicenseUrl", "http://shim/test"));
            }

            return entry;
        }

        async Task PassThrough(IOwinContext context, bool log = false)
        {
            string pathAndQuery = context.Request.Uri.PathAndQuery;
            Uri forwardAddress = new Uri(Address + pathAndQuery);

            Tuple<string, byte[]> content;
            if (!Cache.TryGetValue(forwardAddress, out content))
            {
                content = await Forward(forwardAddress, log);
                Cache[forwardAddress] = content;
            }

            context.Response.ContentType = content.Item1;
            await context.Response.WriteAsync(content.Item2);
        }

        static async Task<Tuple<string, byte[]>> Forward(Uri forwardAddress, bool log)
        {
            Log(forwardAddress, ConsoleColor.Cyan);

            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync(forwardAddress);
            string contentType = response.Content.Headers.ContentType.ToString();
            byte[] data = await response.Content.ReadAsByteArrayAsync();

            if (log)
            {
                using (TextReader reader = new StreamReader(new MemoryStream(data)))
                {
                    string s = reader.ReadToEnd();
                    Console.WriteLine(s);
                }
            }

            return new Tuple<string, byte[]>(contentType, data);
        }

        static void Log(object obj, ConsoleColor color)
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(obj);
            Console.ForegroundColor = previous;
        }
    }
}
