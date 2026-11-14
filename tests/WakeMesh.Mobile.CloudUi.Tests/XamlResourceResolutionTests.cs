using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WakeMesh.Mobile.CloudUi.Tests;

public sealed class XamlResourceResolutionTests
{
    [Fact]
    public void App_global_resources_resolve_all_static_resources_used_by_pages()
    {
        // This is a source-level StaticResource audit, not MAUI XAML instantiation.
        var mobileRoot = FindMobileRoot();
        var app = XDocument.Load(Path.Combine(mobileRoot, "App.xaml"));
        var appResources = app.Descendants().First(e => e.Name.LocalName == "Application.Resources");
        var globalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in appResources.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary"))
        {
            var sourcePath = (string?)source.Attribute("Source");
            if (sourcePath is null) continue;
            var dictionary = LoadXamlDictionary(Path.Combine(mobileRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar)));
            AddDeclaredKeys(dictionary.Root!, globalKeys);
        }
        AddDeclaredKeys(appResources, globalKeys);

        var failures = new List<string>();
        var pagePaths = Directory.EnumerateFiles(Path.Combine(mobileRoot, "Views"), "*.xaml").Append(Path.Combine(mobileRoot, "AppShell.xaml"));
        foreach (var pagePath in pagePaths)
        {
            var page = XDocument.Load(pagePath);
            var pageKeys = new HashSet<string>(globalKeys, StringComparer.Ordinal);
            AddDeclaredKeys(page.Root!, pageKeys);
            foreach (var reference in Regex.Matches(page.ToString(), @"\{StaticResource\s+([^,}\s]+)")
                         .Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
            {
                if (!pageKeys.Contains(reference)) failures.Add($"{Path.GetFileName(pagePath)}: {reference}");
            }
        }

        Assert.True(failures.Count == 0, "Unresolved StaticResource references:\n" + string.Join("\n", failures));
    }

    private static XDocument LoadXamlDictionary(string path)
    {
        var text = File.ReadAllText(path);
        // Colors.xaml uses x:Key but relies on the MAUI XAML compiler's implicit x namespace.
        var namespaces = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
        namespaces.AddNamespace("x", "http://schemas.microsoft.com/winfx/2009/xaml");
        var context = new System.Xml.XmlParserContext(null, namespaces, null, System.Xml.XmlSpace.None);
        using var reader = System.Xml.XmlReader.Create(new StringReader(text), new System.Xml.XmlReaderSettings(), context);
        return XDocument.Load(reader);
    }

    private static void AddDeclaredKeys(XElement root, ISet<string> keys)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var key = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;
            if (key is not null) keys.Add(key);
        }
    }

    private static string FindMobileRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "WakeMesh.Mobile");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
            candidate = Path.Combine(directory.FullName, "WakeMesh", "src", "WakeMesh.Mobile");
            if (File.Exists(Path.Combine(candidate, "App.xaml"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate src/WakeMesh.Mobile/App.xaml from test output.");
    }
}
