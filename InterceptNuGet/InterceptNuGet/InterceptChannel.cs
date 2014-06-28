using Newtonsoft.Json.Linq;
using NuGet.Versioning;
using System;
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
    class InterceptChannel
    {
        string _baseAddress;
        string _passThroughAddress;

        public InterceptChannel(string baseAddress, string passThroughAddress)
        {
            _baseAddress = baseAddress.TrimEnd('/');
            _passThroughAddress = passThroughAddress.TrimEnd('/');
        }

        public async Task GetPackage(InterceptCallContext context, string id, string version)
        {
            context.Log(string.Format("GetPackage: {0} {1}", id, version), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(context, MakeResolverAddress(id));

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

            XElement feed = MakeFeed("Packages", new List<JToken> { desiredPackage }, id);
            await context.WriteResponse(feed);
        }

        public async Task GetLatestVersionPackage(InterceptCallContext context, string id, bool includePrerelease)
        {
            context.Log(string.Format("GetLatestVersionPackage: {0} {1}", id, includePrerelease ? "[include prerelease]" : ""), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(context, MakeResolverAddress(id));

            JToken latest = ExtractLatestVersion(resolverBlob, includePrerelease);

            if (latest == null)
            {
                throw new Exception(string.Format("package {0} not found", id));
            }

            XElement feed = MakeFeed("Packages", new List<JToken> { latest }, id);
            await context.WriteResponse(feed);
        }

        public async Task GetAllPackageVersions(InterceptCallContext context, string id)
        {
            context.Log(string.Format("GetAllPackageVersions: {0}", id), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(context, MakeResolverAddress(id));
            XElement feed = MakeFeed("Packages", resolverBlob["package"], id);
            await context.WriteResponse(feed);
        }

        public async Task GetListOfPackageVersions(InterceptCallContext context, string id)
        {
            context.Log(string.Format("GetListOfPackageVersions: {0}", id), ConsoleColor.Magenta);

            JObject resolverBlob = await FetchJson(context, MakeResolverAddress(id));

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

            await context.WriteResponse(array);
        }

        public async Task GetUpdates(InterceptCallContext context, string[] packageIds, string[] versions, string[] versionConstraints, string[] targetFrameworks, bool includePrerelease, bool includeAllVersions)
        {
            context.Log(string.Format("GetUpdates: {0}", string.Join("|", packageIds)), ConsoleColor.Magenta);

            List<JToken> packages = new List<JToken>();

            for (int i = 0; i < packageIds.Length; i++)
            {
                VersionRange range = null;
                VersionRange.TryParse(versionConstraints[i], out range);

                JObject resolverBlob = await FetchJson(context, MakeResolverAddress(packageIds[i]));
                JToken latest = ExtractLatestVersion(resolverBlob, includePrerelease, range);
                if (latest == null)
                {
                    throw new Exception(string.Format("package {0} not found", packageIds[i]));
                }
                packages.Add(latest);
            }

            XElement feed = MakeFeed("GetUpdates", packages, packageIds);
            await context.WriteResponse(feed);
        }

        public async Task PassThrough(InterceptCallContext context, bool log = false)
        {
            string pathAndQuery = context.RequestUri.PathAndQuery;
            Uri forwardAddress = new Uri(_passThroughAddress + pathAndQuery);

            context.Log(forwardAddress, ConsoleColor.Cyan);

            Tuple<string, byte[]> content = await Forward(forwardAddress, log);

            context.ResponseContentType = content.Item1;
            await context.WriteResponseAsync(content.Item2);
        }

        static JToken ExtractLatestVersion(JObject resolverBlob, bool includePrerelease, VersionRange range = null)
        {
            //  firstly just pick the first one (or the first in range)

            JToken candidateLatest = null;

            if (range == null)
            {
                candidateLatest = resolverBlob["package"].FirstOrDefault();
            }
            else
            {
                foreach (JToken package in resolverBlob["package"])
                {
                    NuGetVersion currentVersion = NuGetVersion.Parse(package["version"].ToString());
                    if (range.Satisfies(currentVersion))
                    {
                        candidateLatest = package;
                        break;
                    }
                }
            }

            if (candidateLatest == null)
            {
                return null;
            }

            //  secondly iterate through package to see if we have a later package

            NuGetVersion candidateLatestVersion = NuGetVersion.Parse(candidateLatest["version"].ToString());

            foreach (JToken package in resolverBlob["package"])
            {
                NuGetVersion currentVersion = NuGetVersion.Parse(package["version"].ToString());

                if (range != null && !range.Satisfies(currentVersion))
                {
                    continue;
                }

                if (includePrerelease)
                {
                    if (currentVersion > candidateLatestVersion)
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
                return null;
            }

            return candidateLatest;
        }

        Uri MakeResolverAddress(string id)
        {
            id = id.ToLowerInvariant();
            Uri resolverBlobAddress = new Uri(string.Format("{0}/{1}.json", _baseAddress, id));
            return resolverBlobAddress;
        }

        async Task<JObject> FetchJson(InterceptCallContext context, Uri address)
        {
            context.Log(address.ToString(), ConsoleColor.Yellow);

            HttpClient client = new HttpClient();
            HttpResponseMessage response = await client.GetAsync(address);
            string json = await response.Content.ReadAsStringAsync();
            JObject obj = JObject.Parse(json);
            return obj;
        }

        XElement MakeFeed(string method, IEnumerable<JToken> packages, string id)
        {
            return MakeFeed(method, packages, Enumerable.Repeat(id, packages.Count()).ToArray());
        }

        XElement MakeFeed(string method, IEnumerable<JToken> packages, string[] id)
        {
            XNamespace atom = XNamespace.Get(@"http://www.w3.org/2005/Atom");
            XElement feed = new XElement(atom + "feed");
            feed.Add(new XElement(atom + "id", string.Format("{0}/api/v2/{1}", _passThroughAddress, method)));
            feed.Add(new XElement(atom + "title", method));
            int i = 0;
            foreach (JToken package in packages)
            {
                feed.Add(MakeEntry(id[i++], package));
            }
            return feed;
        }

        XElement MakeEntry(string id, JToken package)
        {
            XNamespace atom = XNamespace.Get(@"http://www.w3.org/2005/Atom");
            XNamespace d = XNamespace.Get(@"http://schemas.microsoft.com/ado/2007/08/dataservices");
            XNamespace m = XNamespace.Get(@"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata");

            XElement entry = new XElement(atom + "entry");

            entry.Add(new XElement(atom + "id", string.Format("{0}/api/v2/Packages(Id='{1}',Version='{2}')", _passThroughAddress, id, package["version"])));
            entry.Add(new XElement(atom + "title", id));
            entry.Add(new XElement(atom + "author", new XElement(atom + "name", "SHIM")));

            // the content URL should come from the json
            entry.Add(new XElement(atom + "content",
                new XAttribute("type", "application/zip"),
                new XAttribute("src", string.Format("http://www.nuget.org/api/v2/package/{0}/{1}", id, package["version"]))));

            XElement properties = new XElement(m + "properties");
            entry.Add(properties);

            properties.Add(new XElement(d + "Version", package["version"].ToString()));

            // teh following fields should come from the json
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

            // license information should come from the json
            bool license = false;

            properties.Add(new XElement(d + "RequireLicenseAcceptance", new XAttribute(m + "type", "Edm.Boolean"), license.ToString().ToLowerInvariant()));

            if (license)
            {
                properties.Add(new XElement(d + "LicenseUrl", "http://shim/test"));
            }

            // the following properties required for GetUpdates (from the UI)

            // the following properties should come from the json
            bool iconUrl = false;
            if (iconUrl)
            {
                properties.Add(new XElement(d + "IconUrl", "http://tempuri.org/"));
            }

            properties.Add(new XElement(d + "DownloadCount", new XAttribute(m + "type", "Edm.Int32"), 123456));
            properties.Add(new XElement(d + "GalleryDetailsUrl", "http://tempuri.org/"));
            properties.Add(new XElement(d + "Published", new XAttribute(m + "type", "Edm.DateTime"), "2014-02-25T02:04:38.407"));
            properties.Add(new XElement(d + "Tags", "SHIM.Tags"));

            // title is optional, if it is not there the UI uses tje Id
            //properties.Add(new XElement(d + "Title", "SHIM.Title"));
            properties.Add(new XElement(d + "ReleaseNotes", "SHIM.ReleaseNotes"));

            return entry;
        }

        static async Task<Tuple<string, byte[]>> Forward(Uri forwardAddress, bool log)
        {
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
    }
}
