using System.Text.RegularExpressions;
using iCallManager.ICall;

internal static class DiagnosticFixture
{
    public static UiNode Load(string path)
    {
        var stack = new Stack<(int Depth, UiNode Node)>();
        UiNode? root = null;
        foreach (var line in File.ReadLines(path))
        {
            var m = Regex.Match(line, @"^(?<indent> *)#(?<key>\d+) type=(?<type>\d+) role=(?<role>\d+) enabled=(?<enabled>True|False) framework=(?<framework>.*?) name=\[(?<name>.*)\] id=\[(?<id>.*)\] help=\[(?<help>.*?)\](?: value=\[(?<value>.*)\])?$");
            if (!m.Success) continue;
            int depth = m.Groups["indent"].Length / 2;
            var node = new UiNode
            {
                Key = int.Parse(m.Groups["key"].Value), ControlType = int.Parse(m.Groups["type"].Value),
                Role = int.Parse(m.Groups["role"].Value), Enabled = bool.Parse(m.Groups["enabled"].Value),
                Framework = m.Groups["framework"].Value, Name = m.Groups["name"].Value,
                AutomationId = m.Groups["id"].Value, HelpText = m.Groups["help"].Value, Value = m.Groups["value"].Value
            };
            while (stack.Count > 0 && stack.Peek().Depth >= depth) stack.Pop();
            if (stack.Count == 0) { if (root != null) throw new InvalidDataException("Multiple roots"); root = node; }
            else stack.Peek().Node.Children.Add(node);
            stack.Push((depth, node));
        }
        return root ?? throw new InvalidDataException("No diagnostic nodes");
    }
}
