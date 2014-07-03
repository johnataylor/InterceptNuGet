using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace InterceptNuGet
{
    public class InterceptDispatcher
    {
        Tuple<string, Func<InterceptCallContext, Task>>[] _funcs;
        Tuple<string, Func<InterceptCallContext, Task>>[] _feedFuncs;
        InterceptChannel _channel;

        public InterceptDispatcher(string baseAddress, string searchBaseAddress, string passThroughAddress)
        {
            _funcs = new Tuple<string, Func<InterceptCallContext, Task>>[]
            {
                new Tuple<string, Func<InterceptCallContext, Task>>("Search()/$count", Count),
                new Tuple<string, Func<InterceptCallContext, Task>>("Search", Search),
                new Tuple<string, Func<InterceptCallContext, Task>>("FindPackagesById", FindPackagesById),
                new Tuple<string, Func<InterceptCallContext, Task>>("GetUpdates", GetUpdates),
                new Tuple<string, Func<InterceptCallContext, Task>>("Packages", Packages),
                new Tuple<string, Func<InterceptCallContext, Task>>("package-ids", PackageIds),
                new Tuple<string, Func<InterceptCallContext, Task>>("package-versions", PackageVersions),
                new Tuple<string, Func<InterceptCallContext, Task>>("$metadata", Metadata)
            };

            _feedFuncs = new Tuple<string, Func<InterceptCallContext, Task>>[]
            {
                new Tuple<string, Func<InterceptCallContext, Task>>("Search()/$count", Feed_Count),
                new Tuple<string, Func<InterceptCallContext, Task>>("Search", Feed_Search),
                new Tuple<string, Func<InterceptCallContext, Task>>("FindPackagesById", Feed_FindPackagesById),
                new Tuple<string, Func<InterceptCallContext, Task>>("Packages", Feed_Packages),
                new Tuple<string, Func<InterceptCallContext, Task>>("$metadata", Feed_Metadata)
            };

            _channel = new InterceptChannel(baseAddress, searchBaseAddress, passThroughAddress);
        }

        public async Task Invoke(InterceptCallContext context)
        {
            string unescapedAbsolutePath = Uri.UnescapeDataString(context.RequestUri.AbsolutePath);

            string path = unescapedAbsolutePath.Remove(0, "/api/v2/".Length);

            foreach (var func in _funcs)
            {
                if (path == string.Empty)
                {
                    await Root(context);
                    return;
                }
                else if (path.StartsWith(func.Item1))
                {
                    await func.Item2(context);
                    return;
                }
            }

            //  url was not recognized - perhaps this is a feed

            int index1 = path.IndexOf('/', 0) + 1;
            if (index1 < path.Length)
            {
                int index2 = path.IndexOf('/', index1);
                if (index2 < path.Length)
                {
                    path = path.Remove(0, index2 + 1);
                }
            }

            foreach (var func in _feedFuncs)
            {
                if (path == string.Empty)
                {
                    await Feed_Root(context);
                    return;
                }
                if (path.StartsWith(func.Item1))
                {
                    await func.Item2(context);
                    return;
                }
            }

            context.Log("default", ConsoleColor.Red);
            await _channel.PassThrough(context);
        }
        async Task Root(InterceptCallContext context)
        {
            context.Log("Root", ConsoleColor.Green);
            await _channel.Root(context);
        }

        async Task Metadata(InterceptCallContext context)
        {
            context.Log("Metadata", ConsoleColor.Green);
            await _channel.Metadata(context);
        }

        async Task Count(InterceptCallContext context)
        {
            context.Log("Count", ConsoleColor.Green);

            await CountImpl(context);
        }
        async Task Search(InterceptCallContext context)
        {
            context.Log("Search", ConsoleColor.Green);

            await SearchImpl(context);
        }

        async Task CountImpl(InterceptCallContext context, string feed = null)
        {
            IDictionary<string, string> arguments = ExtractArguments(context.RequestUri.Query);

            string searchTerm = Uri.UnescapeDataString(arguments["searchTerm"]).Trim('\'');
            bool isLatestVersion = arguments.Contains(new KeyValuePair<string, string>("$filter", "IsLatestVersion"));
            string targetFramework = Uri.UnescapeDataString(arguments["targetFramework"]).Trim('\'');
            bool includePrerelease = false;
            bool.TryParse(Uri.UnescapeDataString(arguments["includePrerelease"]), out includePrerelease);

            await _channel.Count(context, searchTerm, isLatestVersion, targetFramework, includePrerelease, feed);
        }
        async Task SearchImpl(InterceptCallContext context, string feed = null)
        {
            IDictionary<string, string> arguments = ExtractArguments(context.RequestUri.Query);

            string searchTerm = Uri.UnescapeDataString(arguments["searchTerm"]).Trim('\'');
            bool isLatestVersion = arguments.Contains(new KeyValuePair<string, string>("$filter", "IsLatestVersion"));
            string targetFramework = Uri.UnescapeDataString(arguments["targetFramework"]).Trim('\'');
            bool includePrerelease = false;
            bool.TryParse(Uri.UnescapeDataString(arguments["includePrerelease"]), out includePrerelease);
            int skip = 0;
            int.TryParse(Uri.UnescapeDataString(arguments["$skip"]), out skip);
            int take = 30;
            int.TryParse(Uri.UnescapeDataString(arguments["$top"]), out take);

            await _channel.Search(context, searchTerm, isLatestVersion, targetFramework, includePrerelease, skip, take, feed);
        }

        async Task FindPackagesById(InterceptCallContext context)
        {
            context.Log("FindPackagesById", ConsoleColor.Green);

            //TODO: simplify this code and make it more similar to the other functions

            string[] terms = context.RequestUri.Query.TrimStart('?').Split('&');

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
                await _channel.GetLatestVersionPackage(context, id, isAbsoluteLatestVersion);
            }
            else
            {
                await _channel.GetAllPackageVersions(context, id);
            }
        }

        async Task PackageIds(InterceptCallContext context)
        {
            context.Log("PackageIds", ConsoleColor.Green);

            //  direct this to Lucene

            await _channel.PassThrough(context, true);
        }

        async Task PackageVersions(InterceptCallContext context)
        {
            context.Log("PackageVersions", ConsoleColor.Green);

            string path = context.RequestUri.AbsolutePath;
            string id = path.Substring(path.LastIndexOf("/") + 1);

            await _channel.GetListOfPackageVersions(context, id);
        }

        async Task GetUpdates(InterceptCallContext context)
        {
            context.Log("GetUpdates", ConsoleColor.Green);

            IDictionary<string, string> arguments = ExtractArguments(context.RequestUri.Query);

            string[] packageIds = Uri.UnescapeDataString(arguments["packageIds"]).Trim('\'').Split('|');
            string[] versions = Uri.UnescapeDataString(arguments["versions"]).Trim('\'').Split('|');
            string[] versionConstraints = Uri.UnescapeDataString(arguments["versionConstraints"]).Trim('\'').Split('|');
            string[] targetFrameworks = Uri.UnescapeDataString(arguments["targetFrameworks"]).Trim('\'').Split('|');
            bool includePrerelease = false;
            bool.TryParse(arguments["includePrerelease"], out includePrerelease);
            bool includeAllVersions = false;
            bool.TryParse(arguments["includeAllVersions"], out includeAllVersions);

            await _channel.GetUpdates(context, packageIds, versions, versionConstraints, targetFrameworks, includePrerelease, includeAllVersions);
        }

        async Task Packages(InterceptCallContext context)
        {
            context.Log("Packages", ConsoleColor.Green);

            string path = Uri.UnescapeDataString(context.RequestUri.AbsolutePath);
            string query = context.RequestUri.Query;

            await GetPackage(context, path, query);
        }

        async Task GetPackage(InterceptCallContext context, string path, string query, string feed = null)
        {
            if (path.EndsWith("Packages()"))
            {
                IDictionary<string, string> arguments = ExtractArguments(context.RequestUri.Query);

                string filter = null;
                arguments.TryGetValue("$filter", out filter);

                if (filter == null)
                {
                    await _channel.ListAllVersion(context);
                }
                else if (filter == "IsLatestVersion")
                {
                    await _channel.ListLatestVersion(context);
                }
                else
                {
                    string t = Uri.UnescapeDataString(filter);
                    string s = t.Substring(t.IndexOf("eq") + 2).Trim(' ', '\'');

                    string id = s.ToLowerInvariant();

                    if (id == null)
                    {
                        throw new Exception("unable to find id in query string");
                    }

                    await _channel.GetAllPackageVersions(context, id);
                }
            }
            else
            {
                string args = path.Substring(path.LastIndexOf('(')).Trim('(', ')');

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

                await _channel.GetPackage(context, id, version, feed);
            }
        }

        async Task Feed_Root(InterceptCallContext context)
        {
            context.Log("Feed_Root", ConsoleColor.Green);
            string feed = ExtractFeed(context.RequestUri.AbsolutePath);
            context.Log(string.Format("feed: {0}", feed), ConsoleColor.DarkGreen);
            await _channel.Root(context, feed);
        }

        async Task Feed_Metadata(InterceptCallContext context)
        {
            context.Log("Feed_Metadata", ConsoleColor.Green);
            string feed = ExtractFeed(context.RequestUri.AbsolutePath);
            context.Log(string.Format("feed: {0}", feed), ConsoleColor.DarkGreen);
            await _channel.Metadata(context, feed);
        }

        async Task Feed_Count(InterceptCallContext context)
        {
            context.Log("Feed_Count", ConsoleColor.Green);
            string feed = ExtractFeed(context.RequestUri.AbsolutePath);
            context.Log(string.Format("feed: {0}", feed), ConsoleColor.DarkGreen);
            await CountImpl(context, feed);
        }

        async Task Feed_Search(InterceptCallContext context)
        {
            context.Log("Feed_Search", ConsoleColor.Green);
            string feed = ExtractFeed(context.RequestUri.AbsolutePath);
            context.Log(string.Format("feed: {0}", feed), ConsoleColor.DarkGreen);
            await SearchImpl(context, feed);
        }

        async Task Feed_FindPackagesById(InterceptCallContext context)
        {
            context.Log("Feed_FindPackagesById", ConsoleColor.Green);
            context.Log(string.Format("feed: {0}", ExtractFeed(context.RequestUri.AbsolutePath)), ConsoleColor.DarkGreen);
            await _channel.PassThrough(context);
        }

        async Task Feed_Packages(InterceptCallContext context)
        {
            context.Log("Feed_Packages", ConsoleColor.Green);
            string feed = ExtractFeed(context.RequestUri.AbsolutePath);
            context.Log(string.Format("feed: {0}", feed), ConsoleColor.DarkGreen);

            string path = Uri.UnescapeDataString(context.RequestUri.AbsolutePath);
            path = path.Substring(path.IndexOf(feed) + feed.Length + 1);

            string query = context.RequestUri.Query;

            await GetPackage(context, path, query, feed);
        }

        static string ExtractFeed(string path)
        {
            path = path.Remove(0, "/api/v2/".Length);

            int index1 = path.IndexOf('/', 0) + 1;
            if (index1 < path.Length)
            {
                int index2 = path.IndexOf('/', index1);
                if (index2 < path.Length)
                {
                    string s = path.Substring(0, index2 + 1);
                    string[] t = s.Split('/');
                    if (t.Length > 1)
                    {
                        return t[1];
                    }
                }
            }
            return string.Empty;
        }

        static IDictionary<string, string> ExtractArguments(string query)
        {
            IDictionary<string, string> arguments = new Dictionary<string, string>();
            string[] args = query.TrimStart('?').Split('&');
            foreach (var arg in args)
            {
                string[] val = arg.Split('=');
                arguments[val[0]] = Uri.UnescapeDataString(val[1]);
            }
            return arguments;
        }
    }
}
