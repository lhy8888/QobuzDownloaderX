using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

internal sealed class CheckReport
{
    private readonly List<XElement> cases = new List<XElement>();
    internal void Add(string name, TimeSpan elapsed, Exception error)
    {
        var item = new XElement("testcase", new XAttribute("name", name),
            new XAttribute("time", elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)));
        if (error != null) item.Add(new XElement("failure", new XAttribute("type", error.GetType().Name), error.ToString()));
        cases.Add(item);
    }
    internal void Save(string suite)
    {
        string path = Environment.GetEnvironmentVariable("QBDLX_TEST_REPORT");
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        int failed = cases.Count(c => c.Element("failure") != null);
        new XDocument(new XElement("testsuite", new XAttribute("name", suite),
            new XAttribute("tests", cases.Count), new XAttribute("failures", failed), cases)).Save(path);
        string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summary))
            File.AppendAllText(summary, "\n" + suite + ": " + (cases.Count - failed) + " passed, " + failed + " failed.\n\n" +
                "| Result | Check |\n| --- | --- |\n" + string.Join("\n", cases.Select(c =>
                    "| " + (c.Element("failure") == null ? "PASS" : "FAIL") + " | " + c.Attribute("name").Value.Replace("|", "/") + " |")) + "\n");
    }
}
