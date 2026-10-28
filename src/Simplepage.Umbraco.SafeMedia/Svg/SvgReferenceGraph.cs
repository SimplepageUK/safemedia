using System.Text.RegularExpressions;
using System.Xml;

namespace Simplepage.Umbraco.SafeMedia.Svg;

/// <summary>
/// Works out how many shapes an SVG draws once every reference is expanded, so a "bomb" can be
/// refused. Reported by Liam Laverty (GHSA-5f39-6hx3-pgrw).
///
/// WHY
///
/// &lt;use href="#x"&gt; draws a copy of x. Nest it - a group of ten uses of a group of ten uses
/// of ... - and a two-kilobyte file draws millions of shapes. Every element in it is allowed, so
/// an element allowlist cannot see the problem; the cost is in how the elements refer to each
/// other. Measured in Chrome: 100,000 shapes render in a couple of seconds, 1,000,000 hang the tab.
///
/// HOW
///
/// While the document is read, each element is recorded with its children, its id, and the ids
/// it refers to: href="#x" on any element, url(#x) in any attribute or inline style, and
/// url(#x) in a &lt;style&gt; block. CSS selectors can target any element, so a stylesheet
/// reference is counted against EVERY element - an upper bound, which is the safe direction.
/// The drawn size of an element is 1 + its children + everything it refers to, computed once
/// per element and capped just above the limit so the arithmetic cannot overflow.
///
/// The walk is iterative. A reference chain thousands of links long would overflow the stack in
/// a recursive version, and a stack overflow cannot be caught in .NET - it ends the process.
///
/// Loops are looked for first, using only what the file actually says - children and explicit
/// references - and refused. Sizes are counted second, with the stylesheet upper bound included.
/// That upper bound can create cycles of its own (a shape inside a pattern "references" the
/// pattern via a class), so in the second pass any cycle is skipped: the first pass has already
/// proved it is not a real one.
/// </summary>
internal sealed partial class SvgReferenceGraph
{
    /// <summary>Real artwork nests a few dozen levels at most.</summary>
    internal const int MaxDepth = 256;

    private readonly List<Node> _nodes = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly Stack<int> _open = new();
    private readonly List<string> _stylesheetReferences = new();

    internal enum Verdict
    {
        WithinLimit,
        TooLarge,
        Loop,
    }

    private sealed class Node
    {
        public List<int> Children { get; } = new();

        public List<string> References { get; } = new();
    }

    /// <summary>Call with the reader on an element. Leaves the reader on the element.</summary>
    public void OpenElement(XmlReader reader)
    {
        var index = _nodes.Count;
        var node = new Node();
        _nodes.Add(node);

        if (_open.Count > 0)
        {
            _nodes[_open.Peek()].Children.Add(index);
        }

        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                var name = reader.LocalName;
                var value = reader.Value;

                if (name == "id")
                {
                    // The first element with an id wins, as in browsers.
                    _ids.TryAdd(value.Trim(), index);
                }
                else if (name == "href")
                {
                    var target = value.Trim();
                    if (target.StartsWith('#'))
                    {
                        node.References.Add(target[1..]);
                    }
                }
                else
                {
                    node.References.AddRange(UrlFragments(value));
                }
            }

            reader.MoveToElement();
        }

        if (!reader.IsEmptyElement)
        {
            _open.Push(index);
        }
    }

    public void CloseElement()
    {
        if (_open.Count > 0)
        {
            _open.Pop();
        }
    }

    public void AddStylesheet(string css) => _stylesheetReferences.AddRange(UrlFragments(css));

    public Verdict Evaluate(int limit)
    {
        // Each node's edges are built once, up front. Rebuilding them on every visit made the
        // walk quadratic in the number of children: a <defs> with 100,000 children took
        // seconds, which moves the denial of service from the browser to the server.
        var explicitEdges = new int[_nodes.Count][];
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            var edges = new List<int>(node.Children.Count + node.References.Count);
            edges.AddRange(node.Children);

            foreach (var reference in node.References)
            {
                if (_ids.TryGetValue(reference, out var target))
                {
                    edges.Add(target);
                }
            }

            explicitEdges[i] = edges.ToArray();
        }

        var stylesheetTargets = _stylesheetReferences
            .Select(id => _ids.TryGetValue(id, out var target) ? target : -1)
            .Where(target => target >= 0)
            .Distinct()
            .ToArray();

        if (Walk(explicitEdges, Array.Empty<int>(), limit, detectLoops: true, out _))
        {
            return Verdict.Loop;
        }

        Walk(explicitEdges, stylesheetTargets, limit, detectLoops: false, out var total);
        return total > limit ? Verdict.TooLarge : Verdict.WithinLimit;
    }

    /// <summary>
    /// Iterative post-order walk over explicit edges plus, for every node, the stylesheet
    /// targets. With <paramref name="detectLoops"/>, returns true on reaching a node that is
    /// still in progress. Without it, such a node is skipped (see the class comment) and
    /// <paramref name="total"/> is the shapes drawn, capped just above the limit.
    /// </summary>
    private bool Walk(int[][] explicitEdges, int[] stylesheetTargets, int limit, bool detectLoops, out long total)
    {
        var cap = (long)limit + 1;
        var size = new long[_nodes.Count];
        var state = new byte[_nodes.Count]; // 0 unvisited, 1 in progress, 2 done
        var stack = new Stack<(int Node, int Edge)>();
        total = 0;

        for (var root = 0; root < _nodes.Count; root++)
        {
            if (state[root] != 0)
            {
                continue;
            }

            state[root] = 1;
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (current, edge) = stack.Pop();
                var own = explicitEdges[current];
                var count = own.Length + stylesheetTargets.Length;

                if (edge < count)
                {
                    stack.Push((current, edge + 1));
                    var target = edge < own.Length ? own[edge] : stylesheetTargets[edge - own.Length];

                    if (target == current)
                    {
                        continue;
                    }

                    if (state[target] == 1)
                    {
                        if (detectLoops)
                        {
                            return true;
                        }

                        continue;
                    }

                    if (state[target] == 0)
                    {
                        state[target] = 1;
                        stack.Push((target, 0));
                    }

                    continue;
                }

                long drawn = 1;
                for (var k = 0; k < count; k++)
                {
                    var target = k < own.Length ? own[k] : stylesheetTargets[k - own.Length];
                    if (target != current && state[target] == 2)
                    {
                        drawn = Math.Min(cap, drawn + size[target]);
                    }
                }

                size[current] = drawn;
                state[current] = 2;
            }

            total = Math.Min(cap, total + size[root]);
            if (!detectLoops && total > limit)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The ids named by url(#id) in a CSS value or stylesheet, after CSS escapes are resolved.
    /// Extraction only: whether a url() is allowed at all is decided elsewhere.
    /// </summary>
    private static IEnumerable<string> UrlFragments(string value)
    {
        if (!value.Contains("url", StringComparison.OrdinalIgnoreCase) && !value.Contains('\\'))
        {
            return Array.Empty<string>();
        }

        return UrlFragmentPattern()
            .Matches(SvgSafetyCheck.DecodeCssEscapes(value))
            .Select(m => m.Groups[1].Value);
    }

    [GeneratedRegex(@"url\(\s*['""]?\s*#([^'"")\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlFragmentPattern();
}
